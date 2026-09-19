using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoDedupe.Models;

namespace VideoDedupe.Extraction;

/// <summary>
/// Treats a still image as a one-frame video, which is all the rest of the pipeline
/// needs to dedupe photos: the normalizer and the DCT hasher were never video-specific,
/// they just take a greyscale frame.
///
/// Two deliberate fictions make a still fit a pipeline built around timelines:
///
/// 1. A synthetic non-zero duration is reported. The service rejects anything reporting
///    zero duration as unreadable, which is right for a video and wrong for a photo.
/// 2. The same decoded frame is returned once per requested timestamp. The matcher
///    discards fingerprints with fewer than MinFramesPerVideo frames, so a genuinely
///    single-frame fingerprint would be silently dropped from every scan. Repeating the
///    frame costs one decode and a few microseconds of hashing, and means identical
///    photos produce identical sequences that align trivially.
///
/// Decoding uses WPF's own imaging stack, so it inherits whatever codecs Windows has
/// (JPEG, PNG, BMP, GIF, TIFF always; HEIC/WebP when the relevant Store codec is
/// installed) without adding a dependency.
/// </summary>
public sealed class ImageFrameExtractor : IFrameExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
    };

    /// <summary>
    /// Reported for every image. The value is arbitrary but must be non-zero and long
    /// enough that sample planning produces several timestamps rather than collapsing.
    /// </summary>
    private static readonly TimeSpan SyntheticDuration = TimeSpan.FromSeconds(10);

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public Task<VideoMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var (width, height) = ReadDimensions(path);
            return new VideoMetadata(SyntheticDuration, width, height);
        }, cancellationToken);

    public async IAsyncEnumerable<GrayFrame> ExtractFramesAsync(
        string path,
        IReadOnlyList<TimeSpan> timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var frame = await Task.Run(() => DecodeGray(path), cancellationToken).ConfigureAwait(false);

        foreach (var timestamp in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A fresh GrayFrame per timestamp rather than the same instance repeated, so
            // nothing downstream can be surprised by aliased pixel buffers.
            yield return new GrayFrame(frame.Width, frame.Height, frame.Pixels, timestamp);
        }
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            // DelayCreation + CacheOption.None reads the header only, so this stays cheap
            // on a folder of large photos.
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
                throw new FrameExtractionException("The image contains no frames.");

            var f = decoder.Frames[0];
            return (f.PixelWidth, f.PixelHeight);
        }
        catch (FrameExtractionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FrameExtractionException($"Could not read the image: {ex.Message}", ex);
        }
    }

    private static GrayFrame DecodeGray(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            // OnLoad so the stream can close immediately; the file must not stay locked,
            // because the review panel may want to delete it straight afterwards.
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
                throw new FrameExtractionException("The image contains no frames.");

            BitmapSource source = decoder.Frames[0];

            // Gray8 conversion is done by WPF, which handles the colour-space work and
            // any palette or bit-depth oddity in the source.
            var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

            var width = gray.PixelWidth;
            var height = gray.PixelHeight;
            if (width <= 0 || height <= 0)
                throw new FrameExtractionException("The image has no usable dimensions.");

            var stride = width; // one byte per pixel at Gray8
            var pixels = new byte[stride * height];
            gray.CopyPixels(pixels, stride, 0);

            // Frozen/short-lived: the buffer is what matters from here, not the bitmap.
            return new GrayFrame(width, height, pixels, TimeSpan.Zero);
        }
        catch (FrameExtractionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FrameExtractionException($"Could not decode the image: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        // Nothing to release: every decode is self-contained.
    }
}
