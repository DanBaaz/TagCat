using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoDedupe.Models;

/// <summary>A single sampled frame reduced to a 64-bit perceptual hash.</summary>
public readonly record struct FrameHash(TimeSpan Timestamp, ulong Hash);

/// <summary>
/// The complete content signature for one video file: an ordered sequence of
/// perceptual frame hashes plus the identity information needed to decide whether
/// the fingerprint is still valid for the file on disk.
/// </summary>
public sealed class VideoFingerprint
{
    public required string FilePath { get; init; }

    /// <summary>Your media tagger's own primary key, if you have one. Optional.</summary>
    public long? LibraryFileId { get; init; }

    public long FileSizeBytes { get; init; }

    public DateTime LastModifiedUtc { get; init; }

    /// <summary>Only populated for Express-mode entries - a content scan never reads this.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>
    /// True for a fingerprint built by Express matching rather than actual content analysis.
    /// Frames is empty and Width/Height/Duration are never measured for these - the UI checks
    /// this flag to show "identical file content" instead of a fabricated confidence score
    /// or a resolution/duration nothing ever decoded.
    /// </summary>
    public bool IsExpressMatch { get; init; }

    public TimeSpan Duration { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Ordered frame hashes, ascending by timestamp.</summary>
    public required IReadOnlyList<FrameHash> Frames { get; init; }

    /// <summary>
    /// Identifies the algorithm and settings used. If this does not match the
    /// current profile the fingerprint is stale and must be regenerated, because
    /// hashes produced under different settings are not comparable.
    /// </summary>
    public required string ProfileKey { get; init; }

    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;

    public int FrameCount => Frames.Count;

    /// <summary>
    /// A cheap whole-video signature: the bitwise median of every frame hash.
    /// Useful as a first-pass filter but far too lossy to rely on alone.
    /// </summary>
    public ulong CoarseSignature()
    {
        if (Frames.Count == 0) return 0UL;

        var counts = new int[64];
        foreach (var frame in Frames)
        {
            for (var bit = 0; bit < 64; bit++)
            {
                if ((frame.Hash & (1UL << bit)) != 0) counts[bit]++;
            }
        }

        var half = Frames.Count / 2.0;
        var signature = 0UL;
        for (var bit = 0; bit < 64; bit++)
        {
            if (counts[bit] > half) signature |= 1UL << bit;
        }
        return signature;
    }

    /// <summary>Distinct frame hashes, used for the order-insensitive quick comparison.</summary>
    public HashSet<ulong> DistinctHashes() => Frames.Select(f => f.Hash).ToHashSet();
}
