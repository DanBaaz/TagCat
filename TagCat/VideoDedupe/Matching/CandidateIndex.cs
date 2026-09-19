using System;
using System.Collections.Generic;
using VideoDedupe.Models;

namespace VideoDedupe.Matching;

/// <summary>
/// Locality-sensitive hash index over frame hashes, used to decide which pairs of
/// videos are worth comparing properly.
///
/// The reason this exists: comparing every video against every other is O(n^2), so
/// a 20,000 file library is 200 million pair comparisons. Most of those pairs share
/// no content whatsoever and can be eliminated without ever running an alignment.
///
/// Each 64-bit hash is split into four 16-bit bands. Two hashes within a Hamming
/// distance of 3 must agree exactly on at least one band by the pigeonhole
/// principle, so bucketing by band gives candidate recall with no false negatives
/// at that distance, and good recall in practice at the larger thresholds used here.
/// </summary>
public sealed class CandidateIndex
{
    private const int BandCount = 4;
    private const int BandBits = 16;

    private readonly Dictionary<int, HashSet<int>>[] _bands;
    private readonly List<VideoFingerprint> _entries = [];

    public CandidateIndex()
    {
        _bands = new Dictionary<int, HashSet<int>>[BandCount];
        for (var i = 0; i < BandCount; i++) _bands[i] = [];
    }

    public IReadOnlyList<VideoFingerprint> Entries => _entries;

    public void Add(VideoFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        var index = _entries.Count;
        _entries.Add(fingerprint);

        foreach (var frame in fingerprint.Frames)
        {
            for (var band = 0; band < BandCount; band++)
            {
                var key = BandKey(frame.Hash, band);
                if (!_bands[band].TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    _bands[band][key] = bucket;
                }
                bucket.Add(index);
            }
        }
    }

    /// <summary>
    /// Returns indices of videos sharing at least <paramref name="minimumSharedBands"/>
    /// band hits with the query, excluding the query itself. Only indices greater
    /// than the query index are returned, so each pair is produced exactly once.
    /// </summary>
    public IEnumerable<int> FindCandidates(int queryIndex, int minimumSharedBands = 2)
    {
        var query = _entries[queryIndex];
        var hits = new Dictionary<int, int>();

        foreach (var frame in query.Frames)
        {
            for (var band = 0; band < BandCount; band++)
            {
                var key = BandKey(frame.Hash, band);
                if (!_bands[band].TryGetValue(key, out var bucket)) continue;

                foreach (var other in bucket)
                {
                    if (other <= queryIndex) continue;
                    hits[other] = hits.TryGetValue(other, out var count) ? count + 1 : 1;
                }
            }
        }

        foreach (var (other, count) in hits)
        {
            if (count >= minimumSharedBands) yield return other;
        }
    }

    private static int BandKey(ulong hash, int band) =>
        (int)((hash >> (band * BandBits)) & 0xFFFF);
}
