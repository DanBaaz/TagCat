using System;
using System.Globalization;

namespace VideoDedupe.Options;

/// <summary>
/// The three presets your UI should expose as radio buttons. Everything else in
/// <see cref="DedupeOptions"/> can stay behind an "Advanced" expander.
/// </summary>
public enum ScanDepth
{
    /// <summary>Sparse sampling, order-insensitive comparison. Seconds per file. Misses trims and heavy crops.</summary>
    Quick,

    /// <summary>Moderate sampling with alignment. The sensible default for most libraries.</summary>
    Balanced,

    /// <summary>Dense sampling, crop-tolerant hashing, full alignment sweep. Slow but catches almost everything.</summary>
    Thorough,

    /// <summary>Every value is set explicitly by the caller.</summary>
    Custom
}

/// <summary>How the duplicate list is ordered when handed to the UI.</summary>
public enum DuplicateSort
{
    /// <summary>Biggest space saving first. The most useful default for a cleanup pass.</summary>
    ReclaimableSpace,

    /// <summary>Most confident matches first. Best when you want to bulk-approve the safe ones.</summary>
    Confidence,

    /// <summary>Largest groups first.</summary>
    ClusterSize,

    /// <summary>Longest videos first.</summary>
    Duration,

    /// <summary>Alphabetical by the suggested keeper's path.</summary>
    FileName
}

/// <summary>
/// Every knob the deduplicator exposes. Construct via <see cref="For"/> for the
/// preset behaviour, then override individual properties if the user has opened
/// the advanced panel.
/// </summary>
public sealed class DedupeOptions
{
    // ---- Sampling -------------------------------------------------------

    /// <summary>Seconds between sampled frames. Lower means more frames, more accuracy, more time.</summary>
    public double SampleIntervalSeconds { get; set; } = 2.0;

    /// <summary>Upper bound on frames per video, so a three hour file does not dominate a scan.</summary>
    public int MaxFramesPerVideo { get; set; } = 240;

    /// <summary>Minimum frames required before a video is considered fingerprinted at all.</summary>
    public int MinFramesPerVideo { get; set; } = 4;

    /// <summary>Skip the first and last portion of each video to avoid intros, logos and credits.</summary>
    public double EdgeTrimFraction { get; set; } = 0.02;

    // ---- Normalisation --------------------------------------------------

    /// <summary>Detect and strip letterbox or pillarbox bars before hashing.</summary>
    public bool DetectLetterbox { get; set; } = true;

    /// <summary>
    /// Discard a border of this fraction from each edge before hashing. This is what
    /// buys tolerance to cropping, at the cost of throwing away real signal.
    /// 0.10 handles typical crops; above 0.20 false positives climb sharply.
    /// </summary>
    public double CropInsetFraction { get; set; } = 0.0;

    /// <summary>Frames flatter than this standard deviation are treated as blank and dropped.</summary>
    public double MinFrameVariance { get; set; } = 6.0;

    // ---- Matching -------------------------------------------------------

    /// <summary>
    /// Maximum Hamming distance (out of 64 bits) for two frames to count as the same.
    /// Around 8 is strict, 12 is forgiving, above 16 is mostly noise.
    /// </summary>
    public int FrameHashThreshold { get; set; } = 10;

    /// <summary>Fraction of the shorter video's frames that must match before a pair is reported.</summary>
    public double MinimumSimilarity { get; set; } = 0.85;

    /// <summary>
    /// Slide one fingerprint against the other to find trimmed or offset copies.
    /// Turning this off makes matching much faster but only finds whole-file duplicates.
    /// </summary>
    public bool UseSequenceAlignment { get; set; } = true;

    /// <summary>
    /// Report a match when a short clip is fully contained in a longer video, even
    /// though overall coverage of the longer file is low.
    /// </summary>
    public bool DetectContainedClips { get; set; } = true;

    /// <summary>A contained clip must be at least this many seconds long to be reported.</summary>
    public double MinimumClipSeconds { get; set; } = 10.0;

    /// <summary>
    /// Only compare videos whose durations are within this ratio of each other.
    /// Disabled automatically when <see cref="DetectContainedClips"/> is on.
    /// </summary>
    public double DurationRatioTolerance { get; set; } = 0.25;

    // ---- Execution ------------------------------------------------------

    /// <summary>Parallel fingerprinting workers. Zero means use the processor count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 0;

    /// <summary>Reuse stored fingerprints when the file's size and timestamp are unchanged.</summary>
    public bool UseCachedFingerprints { get; set; } = true;

    // ---- Output ---------------------------------------------------------

    public DuplicateSort SortBy { get; set; } = DuplicateSort.ReclaimableSpace;

    /// <summary>
    /// Identity of the current settings. Fingerprints stored under a different key
    /// cannot be compared against these and will be regenerated.
    /// </summary>
    public string ProfileKey() => string.Create(CultureInfo.InvariantCulture,
        $"v1|i{SampleIntervalSeconds:0.##}|m{MaxFramesPerVideo}|lb{(DetectLetterbox ? 1 : 0)}|ci{CropInsetFraction:0.##}|et{EdgeTrimFraction:0.###}");

    public int EffectiveParallelism =>
        MaxDegreeOfParallelism > 0 ? MaxDegreeOfParallelism : Environment.ProcessorCount;

    /// <summary>Builds the option set for a preset. This is what the simple UI path calls.</summary>
    public static DedupeOptions For(ScanDepth depth) => depth switch
    {
        ScanDepth.Quick => new DedupeOptions
        {
            SampleIntervalSeconds = 10.0,
            MaxFramesPerVideo = 24,
            MinFramesPerVideo = 3,
            DetectLetterbox = true,
            CropInsetFraction = 0.0,
            FrameHashThreshold = 8,
            MinimumSimilarity = 0.90,
            UseSequenceAlignment = false,
            DetectContainedClips = false,
            DurationRatioTolerance = 0.15
        },

        ScanDepth.Balanced => new DedupeOptions
        {
            SampleIntervalSeconds = 3.0,
            MaxFramesPerVideo = 160,
            DetectLetterbox = true,
            CropInsetFraction = 0.06,
            FrameHashThreshold = 10,
            MinimumSimilarity = 0.85,
            UseSequenceAlignment = true,
            DetectContainedClips = true,
            DurationRatioTolerance = 0.35
        },

        ScanDepth.Thorough => new DedupeOptions
        {
            SampleIntervalSeconds = 1.0,
            MaxFramesPerVideo = 600,
            DetectLetterbox = true,
            CropInsetFraction = 0.12,
            FrameHashThreshold = 13,
            MinimumSimilarity = 0.75,
            UseSequenceAlignment = true,
            DetectContainedClips = true,
            MinimumClipSeconds = 5.0,
            DurationRatioTolerance = 1.0
        },

        _ => new DedupeOptions()
    };
}
