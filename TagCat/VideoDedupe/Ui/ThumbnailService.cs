using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VideoDedupe.Extraction;
using VideoDedupe.Models;
namespace VideoDedupe.Ui;

/// <summary>
/// Builds a preview image for the review list.
///
/// This goes through the same decoder chain as the scanner rather than the WinRT
/// thumbnail API, because that API turned out not to work for ordinary files in a
/// desktop app. Reusing the chain means previews appear for exactly the files the
/// scanner could read, with no second class of mysterious failure.
/// </summary>
public static class ThumbnailService
{
    public static async Task<BitmapImage?> GetThumbnailAsync(
        string path,
        int width = 280,
        int height = 160,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var frame = await Task.Run(async () =>
            {
                using var extractor = new CompositeFrameExtractor();

                var metadata = await extractor.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);

                // A third of the way in is far more likely to be representative than
                // the opening frame, which is often black or a logo.
                var position = TimeSpan.FromSeconds(metadata.Duration.TotalSeconds / 3.0);

                await foreach (var decoded in extractor
                    .ExtractFramesAsync(path, [position], cancellationToken)
                    .ConfigureAwait(false))
                {
                    return decoded;
                }

                return null;
            }, cancellationToken).ConfigureAwait(false);

            return frame is null ? null : CreateFrozenBitmap(frame, width, height);
        }
        catch
        {
            // A missing preview is cosmetic. The row still shows the file details.
            return null;
        }
    }

    /// <summary>
    /// Turns the greyscale frame into an image and freezes it. Freezing is what makes
    /// it legal to build here, off the UI thread, and then bind it to a control: a
    /// frozen Freezable is immutable and has no thread affinity.
    /// </summary>
    private static BitmapImage CreateFrozenBitmap(GrayFrame frame, int maxWidth, int maxHeight)
    {
        // Written out as an uncompressed BMP so no image encoder is needed.
        var scale = Math.Min(
            (double)maxWidth / frame.Width,
            (double)maxHeight / frame.Height);

        if (scale > 1) scale = 1;

        var targetWidth = Math.Max(1, (int)(frame.Width * scale));
        var targetHeight = Math.Max(1, (int)(frame.Height * scale));

        // BMP rows are padded to a four-byte boundary and stored bottom-up.
        var rowSize = ((targetWidth * 3) + 3) & ~3;
        var pixelDataSize = rowSize * targetHeight;
        const int headerSize = 54;

        var bmp = new byte[headerSize + pixelDataSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2), headerSize + pixelDataSize);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), headerSize);
        BitConverter.TryWriteBytes(bmp.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bmp.AsSpan(18), targetWidth);
        BitConverter.TryWriteBytes(bmp.AsSpan(22), targetHeight);
        BitConverter.TryWriteBytes(bmp.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(bmp.AsSpan(28), (short)24);
        BitConverter.TryWriteBytes(bmp.AsSpan(34), pixelDataSize);

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = (int)(y * (double)frame.Height / targetHeight);
            var destinationRow = headerSize + ((targetHeight - 1 - y) * rowSize);

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = (int)(x * (double)frame.Width / targetWidth);
                var value = frame[sourceX, sourceY];

                var i = destinationRow + (x * 3);
                bmp[i] = value;
                bmp[i + 1] = value;
                bmp[i + 2] = value;
            }
        }

        using var memory = new MemoryStream(bmp);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = memory;
        bitmap.EndInit();
        bitmap.Freeze();

        return bitmap;
    }
}
