using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Models;
namespace VideoDedupe.Storage;

/// <summary>
/// Persistence for fingerprints. Implement this against your tagger's existing
/// database if you would rather keep everything in one file; the SQLite
/// implementation is provided as a working default, not as a requirement.
/// </summary>
public interface IFingerprintStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored fingerprint only if it is still valid: same profile,
    /// same file size and same modification time. Otherwise null, which signals
    /// that the file must be rehashed.
    /// </summary>
    Task<VideoFingerprint?> GetAsync(
        string filePath,
        long fileSizeBytes,
        DateTime lastModifiedUtc,
        string profileKey,
        CancellationToken cancellationToken = default);

    Task SaveAsync(VideoFingerprint fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Loads every fingerprint generated under the given profile.</summary>
    Task<IReadOnlyList<VideoFingerprint>> GetAllAsync(
        string profileKey,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Drops fingerprints whose files no longer exist. Worth running after a scan.</summary>
    Task<int> PruneMissingFilesAsync(CancellationToken cancellationToken = default);
}
