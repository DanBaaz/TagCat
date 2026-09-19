using System;
using System.Collections.Generic;
using VideoDedupe.Hashing;
using VideoDedupe.Models;
using VideoDedupe.Options;

namespace VideoDedupe.Matching;

/// <summary>Outcome of aligning two frame-hash sequences.</summary>
public readonly record struct AlignmentResult(
    int MatchedFrames,
    int ShorterLength,
    int LongerLength,
    int BestOffset,
    double MeanDistance)
{
    /// <summary>Fraction of the shorter sequence that matched. This is the headline number.</summary>
    public double Similarity => ShorterLength == 0 ? 0 : (double)MatchedFrames / ShorterLength;

    /// <summary>Fraction of the longer sequence covered. Low values indicate a clip inside a longer video.</summary>
    public double Coverage => LongerLength == 0 ? 0 : (double)MatchedFrames / LongerLength;
}

/// <summary>
/// Compares two fingerprints. The order-insensitive path is used for quick scans;
/// the sliding alignment path is what actually catches trimmed and re-cut copies,
/// which is the case a plain whole-file comparison cannot see at all.
/// </summary>
public static class SequenceAligner
{
    /// <summary>
    /// Set-overlap comparison. Ignores ordering entirely, so it is fast and robust
    /// to a missing frame here and there, but it will happily call two videos
    /// identical when one is the other played backwards. Quick scans only.
    /// </summary>
    public static AlignmentResult CompareUnordered(
        IReadOnlyList<FrameHash> left,
        IReadOnlyList<FrameHash> right,
        int threshold)
    {
        var (shorter, longer) = left.Count <= right.Count ? (left, right) : (right, left);

        var matched = 0;
        double distanceTotal = 0;
        var consumed = new bool[longer.Count];

        foreach (var frame in shorter)
        {
            var bestIndex = -1;
            var bestDistance = int.MaxValue;

            for (var i = 0; i < longer.Count; i++)
            {
                if (consumed[i]) continue;

                var distance = HashUtils.HammingDistance(frame.Hash, longer[i].Hash);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                    if (distance == 0) break;
                }
            }

            if (bestIndex >= 0 && bestDistance <= threshold)
            {
                consumed[bestIndex] = true;
                matched++;
                distanceTotal += bestDistance;
            }
        }

        return new AlignmentResult(
            matched,
            shorter.Count,
            longer.Count,
            0,
            matched == 0 ? 64 : distanceTotal / matched);
    }

    /// <summary>
    /// Slides the shorter sequence across the longer one and keeps the offset that
    /// matches the most frames.
    ///
    /// Both sequences are sampled at the same interval, so a shift of one index is a
    /// shift of one sampling period. That is what makes a plain offset sweep valid
    /// here and avoids needing full dynamic-programming alignment: a trimmed copy is
    /// the same sequence starting at a different index, not a warped one.
    ///
    /// Cost is O(n*m) on frame counts, which at the default caps is a few tens of
    /// thousands of 64-bit XORs per pair. Negligible next to decoding.
    /// </summary>
    public static AlignmentResult CompareAligned(
        IReadOnlyList<FrameHash> left,
        IReadOnlyList<FrameHash> right,
        int threshold)
    {
        var (shorter, longer) = left.Count <= right.Count ? (left, right) : (right, left);

        if (shorter.Count == 0 || longer.Count == 0)
        {
            return new AlignmentResult(0, shorter.Count, longer.Count, 0, 64);
        }

        var bestMatched = 0;
        double bestDistanceTotal = 0;
        var bestOffset = 0;

        // Offsets run from fully-before to fully-after so partial overlaps at either
        // end are considered, not just the fully-contained case.
        var minOffset = -(shorter.Count - 1);
        var maxOffset = longer.Count - 1;

        for (var offset = minOffset; offset <= maxOffset; offset++)
        {
            var matched = 0;
            double distanceTotal = 0;
            var overlap = 0;

            for (var i = 0; i < shorter.Count; i++)
            {
                var j = i + offset;
                if (j < 0) continue;
                if (j >= longer.Count) break;

                overlap++;
                var distance = HashUtils.HammingDistance(shorter[i].Hash, longer[j].Hash);
                if (distance <= threshold)
                {
                    matched++;
                    distanceTotal += distance;
                }
            }

            // An overlap of two frames matching perfectly is not evidence of anything.
            if (overlap < 3) continue;

            if (matched > bestMatched)
            {
                bestMatched = matched;
                bestDistanceTotal = distanceTotal;
                bestOffset = offset;
            }
        }

        return new AlignmentResult(
            bestMatched,
            shorter.Count,
            longer.Count,
            bestOffset,
            bestMatched == 0 ? 64 : bestDistanceTotal / bestMatched);
    }

    /// <summary>
    /// Decides what kind of relationship an alignment represents, which drives how
    /// the pair is presented and whether deletion is safe to suggest.
    /// </summary>
    public static MatchRelationship Classify(
        AlignmentResult alignment,
        VideoFingerprint left,
        VideoFingerprint right,
        DedupeOptions options)
    {
        // Both directions well covered means the two files are the same content.
        if (alignment.Coverage >= 0.90 && alignment.Similarity >= 0.90)
        {
            return MatchRelationship.FullMatch;
        }

        // The shorter one is almost fully accounted for, but the longer one is not:
        // that is a clip cut out of a larger video.
        if (alignment.Similarity >= options.MinimumSimilarity && alignment.Coverage < 0.80)
        {
            return MatchRelationship.ContainedClip;
        }

        return MatchRelationship.PartialOverlap;
    }
}
