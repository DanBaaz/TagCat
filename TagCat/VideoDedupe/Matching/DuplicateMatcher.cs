using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VideoDedupe.Models;
using VideoDedupe.Options;

namespace VideoDedupe.Matching;

/// <summary>
/// Turns a set of fingerprints into scored pairs, then into clusters ready for review.
/// Pure computation with no I/O, which makes it straightforward to unit test against
/// hand-built fingerprints.
/// </summary>
public sealed class DuplicateMatcher
{
    private readonly DedupeOptions _options;

    public DuplicateMatcher(DedupeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Finds every pair that clears the configured similarity bar.</summary>
    public IReadOnlyList<DuplicateMatch> FindMatches(
        IReadOnlyList<VideoFingerprint> fingerprints,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);

        var usable = fingerprints
            .Where(f => f.FrameCount >= _options.MinFramesPerVideo)
            .ToList();

        var index = new CandidateIndex();
        foreach (var fingerprint in usable) index.Add(fingerprint);

        var matches = new List<DuplicateMatch>();

        for (var i = 0; i < usable.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var left = usable[i];

            foreach (var j in index.FindCandidates(i))
            {
                var right = usable[j];

                if (!ShouldCompare(left, right)) continue;

                var match = Compare(left, right);
                if (match is not null) matches.Add(match);
            }

            progress?.Report((i + 1) / (double)usable.Count);
        }

        return matches;
    }

    /// <summary>
    /// Cheap rejections before the real comparison runs. The duration gate is skipped
    /// when contained-clip detection is on, since in that mode a large duration
    /// mismatch is the interesting case rather than a disqualifying one.
    /// </summary>
    private bool ShouldCompare(VideoFingerprint left, VideoFingerprint right)
    {
        if (string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase)) return false;

        if (_options.DetectContainedClips) return true;

        var longer = Math.Max(left.Duration.TotalSeconds, right.Duration.TotalSeconds);
        var shorter = Math.Min(left.Duration.TotalSeconds, right.Duration.TotalSeconds);
        if (longer <= 0) return false;

        return (longer - shorter) / longer <= _options.DurationRatioTolerance;
    }

    private DuplicateMatch? Compare(VideoFingerprint left, VideoFingerprint right)
    {
        var alignment = _options.UseSequenceAlignment
            ? SequenceAligner.CompareAligned(left.Frames, right.Frames, _options.FrameHashThreshold)
            : SequenceAligner.CompareUnordered(left.Frames, right.Frames, _options.FrameHashThreshold);

        if (alignment.Similarity < _options.MinimumSimilarity) return null;

        var relationship = SequenceAligner.Classify(alignment, left, right, _options);

        if (relationship == MatchRelationship.ContainedClip)
        {
            if (!_options.DetectContainedClips) return null;

            // A three second clip matching inside a feature film is almost always a
            // coincidence of similar shots rather than a real duplicate.
            var shorterDuration = Math.Min(left.Duration.TotalSeconds, right.Duration.TotalSeconds);
            if (shorterDuration < _options.MinimumClipSeconds) return null;
        }

        if (relationship == MatchRelationship.PartialOverlap && !_options.DetectContainedClips)
        {
            return null;
        }

        var offsetTicks = (long)(alignment.BestOffset * _options.SampleIntervalSeconds * TimeSpan.TicksPerSecond);

        return new DuplicateMatch
        {
            Left = left,
            Right = right,
            Similarity = alignment.Similarity,
            Coverage = alignment.Coverage,
            Offset = TimeSpan.FromTicks(offsetTicks),
            Relationship = relationship,
            MeanDistance = alignment.MeanDistance
        };
    }

    /// <summary>
    /// Groups matches into clusters using union-find, so a chain of A-B and B-C
    /// arrives as one group of three rather than two pairs the user has to
    /// reconcile by hand.
    /// </summary>
    public IReadOnlyList<DuplicateCluster> BuildClusters(IReadOnlyList<DuplicateMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0) return [];

        var nodes = new List<VideoFingerprint>();
        var indexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int GetIndex(VideoFingerprint fingerprint)
        {
            if (indexByPath.TryGetValue(fingerprint.FilePath, out var existing)) return existing;

            var index = nodes.Count;
            nodes.Add(fingerprint);
            indexByPath[fingerprint.FilePath] = index;
            return index;
        }

        foreach (var match in matches)
        {
            GetIndex(match.Left);
            GetIndex(match.Right);
        }

        var parent = new int[nodes.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB) parent[rootB] = rootA;
        }

        foreach (var match in matches)
        {
            Union(indexByPath[match.Left.FilePath], indexByPath[match.Right.FilePath]);
        }

        var membersByRoot = new Dictionary<int, List<VideoFingerprint>>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var root = Find(i);
            if (!membersByRoot.TryGetValue(root, out var list))
            {
                list = [];
                membersByRoot[root] = list;
            }
            list.Add(nodes[i]);
        }

        var matchesByRoot = new Dictionary<int, List<DuplicateMatch>>();
        foreach (var match in matches)
        {
            var root = Find(indexByPath[match.Left.FilePath]);
            if (!matchesByRoot.TryGetValue(root, out var list))
            {
                list = [];
                matchesByRoot[root] = list;
            }
            list.Add(match);
        }

        var clusters = membersByRoot
            .Where(kvp => kvp.Value.Count > 1)
            .Select(kvp => new DuplicateCluster
            {
                Members = kvp.Value,
                Matches = matchesByRoot.TryGetValue(kvp.Key, out var m) ? m : []
            })
            .ToList();

        return Sort(clusters, _options.SortBy);
    }

    /// <summary>Reorders results for display. Cheap enough to re-run whenever the user changes the sort.</summary>
    public static IReadOnlyList<DuplicateCluster> Sort(
        IEnumerable<DuplicateCluster> clusters,
        DuplicateSort sortBy) => sortBy switch
        {
            DuplicateSort.ReclaimableSpace => clusters.OrderByDescending(c => c.ReclaimableBytes).ToList(),
            DuplicateSort.Confidence => clusters.OrderByDescending(c => c.AverageSimilarity).ToList(),
            DuplicateSort.ClusterSize => clusters.OrderByDescending(c => c.Count)
                                                 .ThenByDescending(c => c.ReclaimableBytes).ToList(),
            DuplicateSort.Duration => clusters.OrderByDescending(c => c.SuggestedKeep.Duration).ToList(),
            DuplicateSort.FileName => clusters.OrderBy(c => c.SuggestedKeep.FilePath, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => clusters.ToList()
        };
}
