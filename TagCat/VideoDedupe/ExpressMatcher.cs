using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Models;
using VideoDedupe.Options;

namespace VideoDedupe;

/// <summary>
/// Finds exact duplicates by checksumming file content - never a frame decoded, so it works
/// on any file type and is far cheaper than the content-based scan. Two files with the same
/// checksum are provably byte-for-byte identical; the optional name/date restrictions in
/// ExpressMatchOptions only ever narrow that further, they cannot substitute for it, since
/// this is the one scan mode that can say "identical" rather than "looks alike" with total
/// certainty.
///
/// Files are only hashed if at least one other file shares its exact size first - two files
/// of different sizes can never be identical, so this skips reading the (usually large)
/// majority of a library that has no possible duplicate, which is most of why Express is
/// faster than actually decoding anything.
/// </summary>
public static class ExpressMatcher
{
    /// <summary>Hashing is IO-bound, so a few files at once is faster than one at a time -
    /// capped low for the same reason the thumbnail loader caps its own concurrency: too many
    /// parallel reads on one disk turn into seek thrash rather than more throughput.</summary>
    private const int MaxConcurrency = 4;

    public static ScanResult Scan(
        IReadOnlyList<string> filePaths,
        ExpressMatchOptions options,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var failures = new ConcurrentBag<ScanFailure>();
        var entries = new List<(string Path, FileInfo Info)>();

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    failures.Add(new ScanFailure(path, "File no longer exists."));
                    continue;
                }

                if (options.MinFileSizeBytes is long min && info.Length < min) continue;
                if (options.MaxFileSizeBytes is long max && info.Length > max) continue;

                entries.Add((path, info));
            }
            catch (Exception ex)
            {
                failures.Add(new ScanFailure(path, ex.Message));
            }
        }

        // Pass 1: size alone rules out most files with no possible duplicate, without
        // reading a single byte of their content.
        var sizeGroups = entries.GroupBy(e => e.Info.Length).Where(g => g.Count() > 1);
        var candidates = sizeGroups.SelectMany(g => g).ToList();

        // Pass 2: only the files that survived pass 1 actually get read and hashed.
        var checksums = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var throttle = new SemaphoreSlim(MaxConcurrency);
        try
        {
            var hashTasks = candidates.Select(async entry =>
            {
                await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checksums[entry.Path] = ComputeChecksum(entry.Path);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failures.Add(new ScanFailure(entry.Path, ex.Message));
                }
                finally
                {
                    throttle.Release();
                }
            });

            Task.WhenAll(hashTasks).GetAwaiter().GetResult();
        }
        finally
        {
            throttle.Dispose();
        }

        // Files sharing a checksum are byte-identical; any optional restriction narrows that
        // further, but the checksum itself is always the primary key - nothing here can end
        // up in a cluster with something it isn't actually identical to.
        var clusters = candidates
            .Where(e => checksums.ContainsKey(e.Path))
            .GroupBy(e => checksums[e.Path] + "\u0002" + BuildRestrictionKey(e.Info, options), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateCluster
            {
                Members = g.Select(e => new VideoFingerprint
                {
                    FilePath = e.Path,
                    FileSizeBytes = e.Info.Length,
                    LastModifiedUtc = e.Info.LastWriteTimeUtc,
                    CreatedUtc = e.Info.CreationTimeUtc,
                    Frames = Array.Empty<FrameHash>(),
                    ProfileKey = "express",
                    IsExpressMatch = true
                }).ToList(),
                Matches = Array.Empty<DuplicateMatch>()
            })
            .ToList();

        return new ScanResult
        {
            Clusters = clusters,
            Failures = failures.ToList(),
            FilesScanned = filePaths.Count,
            FingerprintsFromCache = 0,
            FingerprintedCount = entries.Count,
            Elapsed = DateTime.UtcNow - startedAt,
            Diagnostics = Array.Empty<Diagnostics.FileDiagnostic>()
        };
    }

    private static string ComputeChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// The optional additional restrictions, if any are ticked - appended onto the checksum
    /// rather than used alone, so they can only ever split an identical-content group into
    /// smaller ones, never create a match where the content itself differs.
    ///
    /// Timestamps are rounded to the second - filesystems and copy tools do not always
    /// preserve sub-second precision, so comparing to the tick would silently miss files
    /// that are, for every practical purpose, the same timestamp.
    /// </summary>
    private static string BuildRestrictionKey(FileInfo info, ExpressMatchOptions options)
    {
        var parts = new List<string>(3);

        if (options.MatchFileName) parts.Add(info.Name.ToUpperInvariant());
        if (options.MatchCreatedDate) parts.Add(RoundToSecond(info.CreationTimeUtc).ToString("O"));
        if (options.MatchModifiedDate) parts.Add(RoundToSecond(info.LastWriteTimeUtc).ToString("O"));

        return string.Join("\u0001", parts);
    }

    private static DateTime RoundToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}
