using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VideoDedupe.Models;

/// <summary>
/// Describes how two videos relate to one another, which matters a great deal
/// when deciding what to delete. A trimmed clip is not interchangeable with the
/// full version, so the UI should never present them as equal candidates.
/// </summary>
public enum MatchRelationship
{
    /// <summary>Both videos cover essentially the same content end to end.</summary>
    FullMatch,

    /// <summary>The shorter video appears in full inside the longer one.</summary>
    ContainedClip,

    /// <summary>Substantial shared content but neither fully contains the other.</summary>
    PartialOverlap
}

/// <summary>A scored relationship between exactly two fingerprinted videos.</summary>
public sealed class DuplicateMatch
{
    public required VideoFingerprint Left { get; init; }

    public required VideoFingerprint Right { get; init; }

    /// <summary>0.0 to 1.0. Fraction of the shorter video's frames matched under the best alignment.</summary>
    public required double Similarity { get; init; }

    /// <summary>Fraction of the longer video's frames covered by the match.</summary>
    public required double Coverage { get; init; }

    /// <summary>Time offset of Right relative to Left under the best alignment.</summary>
    public TimeSpan Offset { get; init; }

    public required MatchRelationship Relationship { get; init; }

    /// <summary>Mean Hamming distance across matched frame pairs. Lower is a tighter match.</summary>
    public double MeanDistance { get; init; }

    /// <summary>
    /// Of the two files, the one you would normally keep: highest resolution,
    /// then longest duration, then largest file. This is only a suggestion.
    /// </summary>
    public VideoFingerprint SuggestedKeep =>
        Compare(Left, Right) >= 0 ? Left : Right;

    public VideoFingerprint SuggestedRemove =>
        ReferenceEquals(SuggestedKeep, Left) ? Right : Left;

    /// <summary>Bytes reclaimed if the suggested removal is acted on.</summary>
    public long ReclaimableBytes => SuggestedRemove.FileSizeBytes;

    private static int Compare(VideoFingerprint a, VideoFingerprint b)
    {
        var byPixels = ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height);
        if (byPixels != 0) return byPixels;

        var byDuration = a.Duration.CompareTo(b.Duration);
        if (byDuration != 0) return byDuration;

        return a.FileSizeBytes.CompareTo(b.FileSizeBytes);
    }
}

/// <summary>
/// A group of videos that are all transitively related by at least one match.
/// This is the unit the review UI should present, because deleting from a group
/// is a single decision rather than a series of pairwise ones.
/// </summary>
public sealed class DuplicateCluster
{
    public required IReadOnlyList<VideoFingerprint> Members { get; init; }

    public required IReadOnlyList<DuplicateMatch> Matches { get; init; }

    /// <summary>True if this cluster came from Express matching rather than content analysis.
    /// Every member in a single scan's results is homogeneous, so checking the first is enough.</summary>
    public bool IsExpressMatch => Members.Count > 0 && Members[0].IsExpressMatch;

    public int Count => Members.Count;

    /// <summary>Weakest link in the cluster. A low value means it was joined by a marginal match.</summary>
    public double MinimumSimilarity => Matches.Count == 0 ? 1.0 : Matches.Min(m => m.Similarity);

    public double AverageSimilarity => Matches.Count == 0 ? 1.0 : Matches.Average(m => m.Similarity);

    /// <summary>
    /// The member you would normally keep. For a content-based match, that's by resolution
    /// then duration then size, since a lower-quality re-encode is the obvious one to remove.
    ///
    /// An Express match makes that chain meaningless: the files are byte-identical, so size
    /// always ties, and resolution/duration are never measured for these at all - every one
    /// of those criteria ties every time. The filename is the only thing left that might tell
    /// an original from a copy someone made without realising it already existed, so it takes
    /// over as the deciding factor instead, oldest file breaking any remaining tie.
    /// </summary>
    public VideoFingerprint SuggestedKeep => IsExpressMatch
        ? Members
            .OrderByDescending(f => CopyNameRank(f.FilePath))
            .ThenBy(f => f.CreatedUtc)
            .First()
        : Members
            .OrderByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.Duration)
            .ThenByDescending(f => f.FileSizeBytes)
            .First();

    /// <summary>
    /// How "copy-like" a filename looks, worst to best: an auto-generated "(2)"-style suffix
    /// is the strongest sign of an accidental duplicate; a clean name with none of these
    /// patterns is the strongest sign of the original. Checked in this specific order because
    /// a name could technically satisfy more than one pattern, and the worst applicable one is
    /// what should win.
    /// </summary>
    private static int CopyNameRank(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);

        if (Regex.IsMatch(name, @"\s*\(\d+\)$")) return 0;                        // "(2)" at the end
        if (Regex.IsMatch(name, @"_\d+$")) return 1;                              // "_2" at the end
        if (Regex.IsMatch(name, @"-\s*copy$", RegexOptions.IgnoreCase)) return 2; // "- Copy" at the end
        if (Regex.IsMatch(name, @"-\s*copy", RegexOptions.IgnoreCase)) return 3;  // "- Copy" mid-name
        return 4;                                                                 // no copy-like pattern
    }

    /// <summary>Everything except the suggested keeper.</summary>
    public IEnumerable<VideoFingerprint> SuggestedRemovals =>
        Members.Where(m => !ReferenceEquals(m, SuggestedKeep));

    /// <summary>Total bytes recovered if every suggested removal is deleted.</summary>
    public long ReclaimableBytes => SuggestedRemovals.Sum(m => m.FileSizeBytes);

    /// <summary>True if any member is only a partial or trimmed version of another.</summary>
    public bool ContainsPartialMatches =>
        Matches.Any(m => m.Relationship != MatchRelationship.FullMatch);
}
