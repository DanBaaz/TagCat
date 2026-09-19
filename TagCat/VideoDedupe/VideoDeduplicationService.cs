using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Diagnostics;
using VideoDedupe.Extraction;
using VideoDedupe.Hashing;
using VideoDedupe.Matching;
using VideoDedupe.Models;
using VideoDedupe.Options;
using VideoDedupe.Storage;
namespace VideoDedupe;

/// <summary>Progress information suitable for binding straight to a status bar.</summary>
public sealed record ScanProgress(
    ScanPhase Phase,
    int Completed,
    int Total,
    string? CurrentFile = null)
{
    public double Fraction => Total == 0 ? 0 : (double)Completed / Total;
}

public enum ScanPhase
{
    Discovering,
    Fingerprinting,
    Matching,
    Clustering,
    Complete
}

/// <summary>A file that could not be fingerprinted, and why.</summary>
public sealed record ScanFailure(string FilePath, string Reason);

/// <summary>Everything a completed scan produces.</summary>
public sealed class ScanResult
{
    public required IReadOnlyList<DuplicateCluster> Clusters { get; init; }
    public required IReadOnlyList<ScanFailure> Failures { get; init; }
    public required int FilesScanned { get; init; }
    public required int FingerprintsFromCache { get; init; }
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Per-file record of what was decoded. The first thing to look at when a scan finds nothing.</summary>
    public IReadOnlyList<FileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Where the full text log was written.</summary>
    public string? LogPath { get; init; }

    /// <summary>How many files produced a usable fingerprint.</summary>
    public int FingerprintedCount { get; init; }

    public long TotalReclaimableBytes => Clusters.Sum(c => c.ReclaimableBytes);
    public int TotalDuplicateFiles => Clusters.Sum(c => c.Count - 1);
}

/// <summary>
/// The single entry point the host application talks to. Construct one, hand it a
/// list of file paths, and it returns clusters ready to display.
/// </summary>
public sealed class VideoDeduplicationService : IDisposable
{
    private readonly IFrameExtractor _extractor;
    private readonly IFingerprintStore _store;
    private readonly IPerceptualHasher _hasher;
    private bool _initialized;

    public VideoDeduplicationService(
        IFrameExtractor extractor,
        IFingerprintStore store,
        IPerceptualHasher? hasher = null)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hasher = hasher ?? new DctPerceptualHasher();
    }

    /// <summary>
    /// Convenience factory. Uses the Windows decoder with an automatic ffmpeg
    /// fallback if one is present on the machine, and a SQLite fingerprint cache.
    /// </summary>
    public static VideoDeduplicationService CreateDefault(string fingerprintDatabasePath) =>
        new(new CompositeFrameExtractor(), new SqliteFingerprintStore(fingerprintDatabasePath));

    /// <summary>Runs a full scan over the supplied files.</summary>
    public async Task<ScanResult> ScanAsync(
        IEnumerable<string> filePaths,
        DedupeOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(options);

        var startedAt = DateTime.UtcNow;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new ScanProgress(ScanPhase.Discovering, 0, 0));

        var supported = _extractor.SupportedExtensions
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = filePaths
            .Where(p => supported.Contains(Path.GetExtension(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fingerprints = new ConcurrentBag<VideoFingerprint>();
        var failures = new ConcurrentBag<ScanFailure>();
        var diagnostics = new ConcurrentBag<FileDiagnostic>();
        var log = new ScanLog();
        var cacheHits = 0;
        var completed = 0;

        log.Write($"Profile: {options.ProfileKey()}");
        log.Write($"Files to examine: {files.Count}");
        if (_extractor is CompositeFrameExtractor composite)
        {
            log.Write($"Decoders available, in order: {composite.StrategyList}");
            foreach (var note in composite.StartupNotes) log.Write(note);
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.EffectiveParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(files, parallelOptions, async (path, token) =>
        {
            try
            {
                var (fingerprint, fromCache, diagnostic) =
                    await GetOrCreateFingerprintAsync(path, options, token).ConfigureAwait(false);

                diagnostics.Add(diagnostic);
                log.Write(diagnostic);

                if (fingerprint is not null)
                {
                    fingerprints.Add(fingerprint);
                    if (fromCache) Interlocked.Increment(ref cacheHits);
                }
                else
                {
                    failures.Add(new ScanFailure(path, diagnostic.Error ?? "No usable frames could be decoded."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new ScanFailure(path, ex.Message));
                var diagnostic = new FileDiagnostic(path, false, TimeSpan.Zero, 0, 0, 0, 0, 0, ex.Message);
                diagnostics.Add(diagnostic);
                log.Write(diagnostic);
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ScanProgress(ScanPhase.Fingerprinting, done, files.Count, path));
            }
        }).ConfigureAwait(false);

        var all = fingerprints.ToList();

        log.Write($"Fingerprinted {all.Count} of {files.Count} files.");

        progress?.Report(new ScanProgress(ScanPhase.Matching, 0, all.Count));

        var matcher = new DuplicateMatcher(options);
        var matchProgress = new Progress<double>(fraction =>
            progress?.Report(new ScanProgress(ScanPhase.Matching, (int)(fraction * all.Count), all.Count)));

        var matches = await Task.Run(
            () => matcher.FindMatches(all, matchProgress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        log.Write($"Found {matches.Count} matching pairs.");

        progress?.Report(new ScanProgress(ScanPhase.Clustering, 0, matches.Count));

        var clusters = matcher.BuildClusters(matches);

        log.Write($"Grouped into {clusters.Count} clusters.");

        string? logPath = null;
        try
        {
            logPath = log.Save();
        }
        catch
        {
            // A log that cannot be written must not fail the scan.
        }

        progress?.Report(new ScanProgress(ScanPhase.Complete, files.Count, files.Count));

        return new ScanResult
        {
            Clusters = clusters,
            Failures = failures.ToList(),
            FilesScanned = files.Count,
            FingerprintsFromCache = cacheHits,
            FingerprintedCount = all.Count,
            Diagnostics = diagnostics.ToList(),
            LogPath = logPath,
            Elapsed = DateTime.UtcNow - startedAt
        };
    }

    /// <summary>
    /// Fingerprints a single file, reusing the cached result when the file is
    /// unchanged. Exposed separately so the host app can fingerprint during its own
    /// import or metadata pass rather than in a dedicated second sweep.
    /// </summary>
    public async Task<VideoFingerprint?> FingerprintAsync(
        string path,
        DedupeOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var (fingerprint, _, _) = await GetOrCreateFingerprintAsync(path, options, cancellationToken).ConfigureAwait(false);
        return fingerprint;
    }

    /// <summary>
    /// Examines one file and reports exactly what happened, without consulting or
    /// writing the cache. This is the "why did it find nothing?" tool.
    /// </summary>
    public async Task<FileDiagnostic> TestFileAsync(
        string path,
        DedupeOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var probe = new DedupeOptions
        {
            SampleIntervalSeconds = options.SampleIntervalSeconds,
            MaxFramesPerVideo = options.MaxFramesPerVideo,
            MinFramesPerVideo = options.MinFramesPerVideo,
            EdgeTrimFraction = options.EdgeTrimFraction,
            DetectLetterbox = options.DetectLetterbox,
            CropInsetFraction = options.CropInsetFraction,
            MinFrameVariance = options.MinFrameVariance,
            UseCachedFingerprints = false
        };

        try
        {
            var (_, _, diagnostic) = await GetOrCreateFingerprintAsync(path, probe, cancellationToken)
                .ConfigureAwait(false);
            return diagnostic;
        }
        catch (Exception ex)
        {
            return new FileDiagnostic(path, false, TimeSpan.Zero, 0, 0, 0, 0, 0, ex.Message);
        }
    }

    private async Task<(VideoFingerprint? Fingerprint, bool FromCache, FileDiagnostic Diagnostic)> GetOrCreateFingerprintAsync(
        string path,
        DedupeOptions options,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("File no longer exists.", path);

        var profileKey = options.ProfileKey();

        if (options.UseCachedFingerprints)
        {
            var cached = await _store
                .GetAsync(path, info.Length, info.LastWriteTimeUtc, profileKey, cancellationToken)
                .ConfigureAwait(false);

            if (cached is not null)
            {
                return (cached, true, new FileDiagnostic(
                    path, true, cached.Duration, cached.Width, cached.Height,
                    cached.FrameCount, cached.FrameCount, cached.FrameCount, null));
            }
        }

        VideoMetadata metadata;
        try
        {
            metadata = await _extractor.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, false, new FileDiagnostic(path, false, TimeSpan.Zero, 0, 0, 0, 0, 0,
                $"Could not read the video: {ex.Message}"));
        }

        if (metadata.Duration <= TimeSpan.Zero)
        {
            return (null, false, new FileDiagnostic(path, false, TimeSpan.Zero,
                metadata.Width, metadata.Height, 0, 0, 0,
                "The file reported a zero duration, so no frames could be sampled."));
        }

        var timestamps = PlanSampleTimestamps(metadata.Duration, options);

        var frames = new List<FrameHash>(timestamps.Count);
        var decoded = 0;
        string? error = null;

        try
        {
            await foreach (var frame in _extractor
                .ExtractFramesAsync(path, timestamps, cancellationToken)
                .ConfigureAwait(false))
            {
                decoded++;

                // Fades, black frames and solid title cards hash to near-identical
                // values and would match across completely unrelated videos, so they
                // are dropped rather than allowed to inflate similarity scores.
                if (frame.LuminanceStdDev() < options.MinFrameVariance) continue;

                frames.Add(new FrameHash(frame.Timestamp, _hasher.Hash(frame, options)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = $"Decoding stopped early: {ex.Message}";
        }

        if (frames.Count < options.MinFramesPerVideo)
        {
            error ??= decoded == 0
                ? "No frames could be decoded from this file."
                : $"Only {frames.Count} usable frames from {decoded} decoded — the rest were blank or near-blank.";

            return (null, false, new FileDiagnostic(
                path, false, metadata.Duration, metadata.Width, metadata.Height,
                timestamps.Count, decoded, frames.Count, error));
        }

        var fingerprint = new VideoFingerprint
        {
            FilePath = path,
            FileSizeBytes = info.Length,
            LastModifiedUtc = info.LastWriteTimeUtc,
            Duration = metadata.Duration,
            Width = metadata.Width,
            Height = metadata.Height,
            Frames = frames,
            ProfileKey = profileKey
        };

        if (options.UseCachedFingerprints)
        {
            await _store.SaveAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        }

        return (fingerprint, false, new FileDiagnostic(
            path, true, metadata.Duration, metadata.Width, metadata.Height,
            timestamps.Count, decoded, frames.Count, null));
    }

    /// <summary>
    /// Chooses which points in the video to sample. Sampling is uniform rather than
    /// scene-based so that two copies of the same content, encoded differently, land
    /// on the same positions and their hash sequences stay index-aligned. That
    /// property is what makes the offset sweep in the aligner valid.
    /// </summary>
    private static List<TimeSpan> PlanSampleTimestamps(TimeSpan duration, DedupeOptions options)
    {
        var totalSeconds = duration.TotalSeconds;
        var trim = totalSeconds * options.EdgeTrimFraction;
        var start = trim;
        var end = totalSeconds - trim;

        if (end <= start)
        {
            start = 0;
            end = totalSeconds;
        }

        var usable = end - start;
        var interval = options.SampleIntervalSeconds;

        var count = (int)Math.Floor(usable / interval) + 1;

        // Short videos would otherwise produce one or two samples, which is not enough
        // to establish anything, so the interval is compressed to hit the minimum.
        if (count < options.MinFramesPerVideo)
        {
            count = options.MinFramesPerVideo;
            interval = usable / Math.Max(1, count - 1);
        }

        if (count > options.MaxFramesPerVideo)
        {
            count = options.MaxFramesPerVideo;
            interval = usable / Math.Max(1, count - 1);
        }

        var timestamps = new List<TimeSpan>(count);
        for (var i = 0; i < count; i++)
        {
            var seconds = start + (i * interval);
            if (seconds >= totalSeconds) seconds = totalSeconds - 0.05;
            if (seconds < 0) seconds = 0;
            timestamps.Add(TimeSpan.FromSeconds(seconds));
        }

        return timestamps;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public void Dispose() => _extractor.Dispose();
}
