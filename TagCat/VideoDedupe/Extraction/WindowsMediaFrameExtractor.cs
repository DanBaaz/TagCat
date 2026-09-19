using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
namespace VideoDedupe.Extraction;

/// <summary>
/// Frame extraction using the media APIs that ship with Windows. Nothing here is
/// third-party, so there is no licensing consideration when the host app is sold.
///
/// Codec coverage is whatever the machine has installed. That covers H.264, HEVC
/// (with the free Microsoft extension), VP9, WMV and MPEG-4 on a normal Windows 11
/// box, but not exotic containers. Files that fail to open surface as
/// <see cref="FrameExtractionException"/> and should be reported to the user rather
/// than silently skipped, so they know that part of the library went unscanned.
///
/// Requires a TFM of net8.0-windows10.0.19041.0 or later.
/// </summary>
public sealed class WindowsMediaFrameExtractor : IFrameExtractor
{
    /// <summary>
    /// Frames are requested at this width from the decoder. Requesting a small
    /// thumbnail rather than a full frame lets the OS scaler do the expensive
    /// downscale, which is dramatically faster than decoding at native resolution.
    /// It stays well above the 32px hash grid so no detail is lost that matters.
    /// </summary>
    private const int DecodeWidth = 160;
    private const int DecodeHeight = 160;

    private static readonly string[] Extensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".asf", ".3gp", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts"
    ];

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public async Task<VideoMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);

            var encoding = clip.GetVideoEncodingProperties();
            var width = (int)(encoding?.Width ?? 0);
            var height = (int)(encoding?.Height ?? 0);

            return new VideoMetadata(clip.OriginalDuration, width, height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FrameExtractionException($"Could not read video metadata from '{path}'.", ex);
        }
    }

    public async IAsyncEnumerable<GrayFrame> ExtractFramesAsync(
        string path,
        IReadOnlyList<TimeSpan> timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        MediaComposition composition;
        MediaClip clip;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
            composition = new MediaComposition();
            composition.Clips.Add(clip);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FrameExtractionException($"Could not open '{path}' for frame extraction.", ex);
        }

        Exception? firstFailure = null;
        var yielded = 0;

        foreach (var timestamp in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GrayFrame? frame = null;
            try
            {
                frame = await DecodeSingleFrameAsync(composition, timestamp, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Earlier versions swallowed this outright, which hid the fact that
                // this API returns nothing at all for ordinary files in unpackaged
                // desktop apps. The first failure is kept and rethrown below if not a
                // single frame comes back, so the caller learns the actual reason.
                firstFailure ??= ex;
            }

            if (frame is not null)
            {
                yielded++;
                yield return frame;
            }
        }

        if (yielded == 0)
        {
            throw new FrameExtractionException(
                firstFailure is null
                    ? "The Windows thumbnail API returned no frames for this file."
                    : $"The Windows thumbnail API failed: {firstFailure.Message}",
                firstFailure);
        }
    }

    private static async Task<GrayFrame?> DecodeSingleFrameAsync(
        MediaComposition composition,
        TimeSpan timestamp,
        CancellationToken cancellationToken)
    {
        using var stream = await composition
            .GetThumbnailAsync(timestamp, DecodeWidth, DecodeHeight, VideoFramePrecision.NearestFrame)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (stream is null || stream.Size == 0) return null;

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore).AsTask(cancellationToken).ConfigureAwait(false);

        return ToGrayFrame(bitmap, timestamp);
    }

    private static unsafe GrayFrame ToGrayFrame(SoftwareBitmap bitmap, TimeSpan timestamp)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        var description = buffer.GetPlaneDescription(0);

        // IMemoryBufferByteAccess is the documented route to the raw bytes of a
        // SoftwareBitmap. The pointer is only valid while the reference is alive,
        // so the greyscale copy is taken before either is disposed.
        ((IMemoryBufferByteAccess)reference).GetBuffer(out var dataInBytes, out var capacity);

        var span = new ReadOnlySpan<byte>(dataInBytes, (int)capacity);

        return GrayFrame.FromBgra32(
            span,
            description.Width,
            description.Height,
            description.Stride,
            timestamp);
    }

    public void Dispose()
    {
        // MediaComposition and MediaClip are per-call and collected normally.
    }
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
