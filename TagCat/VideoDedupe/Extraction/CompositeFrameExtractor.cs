using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Models;
namespace VideoDedupe.Extraction;

/// <summary>
/// Tries several decoders in order and uses whichever one actually produces frames.
///
/// The order matters and was arrived at the hard way. Media Foundation goes first
/// because it is the decoder Windows itself uses and works reliably in ordinary
/// desktop apps. The WinRT thumbnail API is kept only as a secondary, because it
/// silently returns nothing for plain H.264 MP4 files outside packaged apps. ffmpeg
/// is last, used only if the user already has it installed.
///
/// Licensing: Media Foundation and WinRT ship with Windows. ffmpeg is invoked as a
/// separate process the user supplies; nothing is bundled or linked, so none of this
/// creates an obligation if the app is sold.
/// </summary>
public sealed class CompositeFrameExtractor : IFrameExtractor
{
    private readonly List<(string Name, IFrameExtractor Extractor)> _strategies = [];
    private readonly FfmpegFrameExtractor? _ffmpeg;

    public CompositeFrameExtractor(string? ffmpegPath = null, string? ffprobePath = null)
    {
        // Images first: it is the only strategy that claims image extensions, and
        // StrategiesFor below means video files never reach it anyway.
        _strategies.Add(("Still image", new ImageFrameExtractor()));

        try
        {
            _strategies.Add(("Media Foundation", new MediaFoundationFrameExtractor()));
        }
        catch (Exception ex)
        {
            StartupNotes.Add($"Media Foundation unavailable: {ex.Message}");
        }

        _strategies.Add(("Windows thumbnail API", new WindowsMediaFrameExtractor()));

        ffmpegPath ??= FindOnPath("ffmpeg.exe");
        ffprobePath ??= FindOnPath("ffprobe.exe");

        if (ffmpegPath is not null && ffprobePath is not null)
        {
            _ffmpeg = new FfmpegFrameExtractor(ffmpegPath, ffprobePath);
            _strategies.Add(("ffmpeg", _ffmpeg));
            FfmpegPath = ffmpegPath;
        }
    }

    /// <summary>Notes gathered while setting up, surfaced in the diagnostics dialog.</summary>
    public List<string> StartupNotes { get; } = [];

    /// <summary>Null when no ffmpeg was found on the machine.</summary>
    public string? FfmpegPath { get; }

    public bool HasFallback => _ffmpeg is not null;

    /// <summary>
    /// Whether an ffmpeg pair is reachable right now, without building an extractor. The
    /// scan window uses this to decide whether suggesting ffmpeg would even help - there is
    /// no point offering it to someone who already has it, since the scan would already
    /// have used it. Re-checked on demand rather than cached, so installing ffmpeg
    /// mid-session is picked up without a restart.
    /// </summary>
    public static bool IsFfmpegAvailable() =>
        FindOnPath("ffmpeg.exe") is not null && FindOnPath("ffprobe.exe") is not null;

    /// <summary>Which decoder last produced frames. Useful in the log.</summary>
    public string? LastSuccessfulStrategy { get; private set; }

    public string StrategyList => string.Join(", ", _strategies.ConvertAll(s => s.Name));

    public IReadOnlyCollection<string> SupportedExtensions
    {
        get
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, extractor) in _strategies)
            {
                foreach (var extension in extractor.SupportedExtensions) all.Add(extension);
            }
            return all;
        }
    }

    /// <summary>
    /// Only the strategies that claim this file's extension. Without this every image
    /// would be pushed through Media Foundation and the WinRT thumbnail API first and
    /// fail in both before reaching the image decoder, turning a folder of photos into
    /// thousands of pointless decode attempts (and a log full of misleading errors).
    /// Falls back to trying everything for an unrecognised extension, which is what the
    /// old behaviour was for every file.
    /// </summary>
    private List<(string Name, IFrameExtractor Extractor)> StrategiesFor(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return _strategies;

        var matching = _strategies
            // Explicit OrdinalIgnoreCase: SupportedExtensions is exposed as
            // IReadOnlyCollection, which has no Contains of its own, so this binds to
            // Enumerable.Contains - and that defaults to case-sensitive comparison,
            // which would miss ".JPG" even though the backing set is case-insensitive.
            .FindAll(s => s.Extractor.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));

        return matching.Count > 0 ? matching : _strategies;
    }

    public async Task<VideoMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var errors = new StringBuilder();

        foreach (var (name, extractor) in StrategiesFor(path))
        {
            try
            {
                var metadata = await extractor.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);

                // Opening the container successfully is not the same as understanding
                // the video stream, so a zero duration or size counts as a failure.
                if (metadata.Duration > TimeSpan.Zero && metadata.Width > 0)
                {
                    return metadata;
                }

                errors.Append($"[{name}: reported an empty video stream] ");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Append($"[{name}: {ex.Message}] ");
            }
        }

        throw new FrameExtractionException($"No decoder could read this video. {errors}".TrimEnd());
    }

    public async IAsyncEnumerable<GrayFrame> ExtractFramesAsync(
        string path,
        IReadOnlyList<TimeSpan> timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var errors = new StringBuilder();

        foreach (var (name, extractor) in StrategiesFor(path))
        {
            // Frames are collected into a list before being handed on, because a
            // strategy that yields three frames and then fails must not leave the
            // caller with a half-built fingerprint. Either it works or we move on.
            List<GrayFrame> collected;

            try
            {
                collected = await CollectAsync(extractor, path, timestamps, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Append($"[{name}: {ex.Message}] ");
                continue;
            }

            if (collected.Count > 0)
            {
                LastSuccessfulStrategy = name;
                foreach (var frame in collected) yield return frame;
                yield break;
            }

            errors.Append($"[{name}: returned no frames] ");
        }

        throw new FrameExtractionException($"No decoder could pull frames from this video. {errors}".TrimEnd());
    }

    private static async Task<List<GrayFrame>> CollectAsync(
        IFrameExtractor extractor,
        string path,
        IReadOnlyList<TimeSpan> timestamps,
        CancellationToken cancellationToken)
    {
        var frames = new List<GrayFrame>(timestamps.Count);

        await foreach (var frame in extractor
            .ExtractFramesAsync(path, timestamps, cancellationToken)
            .ConfigureAwait(false))
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Looks for an executable on PATH and in a few conventional install spots.</summary>
    private static string? FindOnPath(string executable)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        var candidates = new List<string>();
        candidates.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        // Common manual-install locations, since ffmpeg is very often unzipped
        // somewhere convenient rather than properly installed.
        candidates.Add(@"C:\ffmpeg\bin");
        candidates.Add(@"C:\Program Files\ffmpeg\bin");
        candidates.Add(AppContext.BaseDirectory);

        foreach (var directory in candidates)
        {
            try
            {
                var full = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // A malformed PATH entry should not stop the search.
            }
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var (_, extractor) in _strategies) extractor.Dispose();
    }
}
