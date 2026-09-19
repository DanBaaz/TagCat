using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Models;
namespace VideoDedupe.Extraction;

/// <summary>Basic properties read from a video before any frames are pulled.</summary>
public sealed record VideoMetadata(TimeSpan Duration, int Width, int Height);

/// <summary>
/// Decodes frames from a video file. Kept deliberately small so alternative
/// backends can be swapped in without touching the rest of the pipeline.
/// </summary>
public interface IFrameExtractor : IDisposable
{
    /// <summary>File extensions this backend expects to be able to open, lowercase with dots.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>Reads duration and dimensions without decoding picture data.</summary>
    Task<VideoMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes a greyscale frame at each requested timestamp, in order. Timestamps
    /// that cannot be decoded are skipped rather than throwing, because a handful of
    /// bad seeks should not fail an entire fingerprint.
    /// </summary>
    IAsyncEnumerable<GrayFrame> ExtractFramesAsync(
        string path,
        IReadOnlyList<TimeSpan> timestamps,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a file cannot be opened or contains no usable video stream.</summary>
public sealed class FrameExtractionException : Exception
{
    public FrameExtractionException(string message, Exception? inner = null)
        : base(message, inner) { }
}
