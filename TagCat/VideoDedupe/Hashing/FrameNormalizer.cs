using System;
using VideoDedupe.Models;
using VideoDedupe.Options;

namespace VideoDedupe.Hashing;

/// <summary>
/// Turns an arbitrary decoded frame into a small fixed-size greyscale grid.
/// This stage is what makes resolution, aspect ratio and letterboxing irrelevant
/// to the hash, so it does most of the real work of the matcher.
/// </summary>
public static class FrameNormalizer
{
    /// <summary>Luminance below which a pixel is treated as part of a black bar.</summary>
    private const int BarLuminanceThreshold = 26;

    /// <summary>Fraction of a row that must be dark before the row counts as a bar.</summary>
    private const double BarRowPurity = 0.94;

    /// <summary>Never strip more than this fraction from any single edge.</summary>
    private const double MaxBarFraction = 0.30;

    /// <summary>
    /// Applies letterbox removal, then the optional crop inset, then downscales to
    /// the requested square size. The result is a fresh buffer of size*size bytes.
    /// </summary>
    public static byte[] Normalize(GrayFrame frame, int size, DedupeOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

        var left = 0;
        var top = 0;
        var right = frame.Width;
        var bottom = frame.Height;

        if (options.DetectLetterbox)
        {
            (left, top, right, bottom) = DetectContentBounds(frame);
        }

        if (options.CropInsetFraction > 0)
        {
            var w = right - left;
            var h = bottom - top;
            var insetX = (int)(w * options.CropInsetFraction);
            var insetY = (int)(h * options.CropInsetFraction);

            // Only apply the inset if it leaves a usable region behind.
            if (w - (insetX * 2) >= size && h - (insetY * 2) >= size)
            {
                left += insetX;
                right -= insetX;
                top += insetY;
                bottom -= insetY;
            }
        }

        return BoxResize(frame, left, top, right, bottom, size);
    }

    /// <summary>
    /// Scans inward from each edge looking for uniformly dark rows and columns.
    /// Falls back to the full frame if the result would be degenerate.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom) DetectContentBounds(GrayFrame frame)
    {
        var maxTrimY = (int)(frame.Height * MaxBarFraction);
        var maxTrimX = (int)(frame.Width * MaxBarFraction);

        var top = 0;
        while (top < maxTrimY && IsDarkRow(frame, top)) top++;

        var bottom = frame.Height;
        while (bottom > frame.Height - maxTrimY && IsDarkRow(frame, bottom - 1)) bottom--;

        var left = 0;
        while (left < maxTrimX && IsDarkColumn(frame, left, top, bottom)) left++;

        var right = frame.Width;
        while (right > frame.Width - maxTrimX && IsDarkColumn(frame, right - 1, top, bottom)) right--;

        // A frame that is genuinely almost all black will trim to nothing. Keep it whole.
        if (right - left < 16 || bottom - top < 16)
        {
            return (0, 0, frame.Width, frame.Height);
        }

        return (left, top, right, bottom);
    }

    private static bool IsDarkRow(GrayFrame frame, int y)
    {
        var dark = 0;
        var start = y * frame.Width;
        for (var x = 0; x < frame.Width; x++)
        {
            if (frame.Pixels[start + x] <= BarLuminanceThreshold) dark++;
        }
        return dark >= frame.Width * BarRowPurity;
    }

    private static bool IsDarkColumn(GrayFrame frame, int x, int top, int bottom)
    {
        var dark = 0;
        var height = bottom - top;
        if (height <= 0) return false;

        for (var y = top; y < bottom; y++)
        {
            if (frame.Pixels[(y * frame.Width) + x] <= BarLuminanceThreshold) dark++;
        }
        return dark >= height * BarRowPurity;
    }

    /// <summary>
    /// Area-average downscale of the given region into a size*size grid. A box
    /// filter is used rather than nearest-neighbour because point sampling makes
    /// the hash unstable across different source resolutions, which is precisely
    /// the thing this whole pipeline is trying to be immune to.
    /// </summary>
    private static byte[] BoxResize(GrayFrame frame, int left, int top, int right, int bottom, int size)
    {
        var output = new byte[size * size];
        var regionWidth = right - left;
        var regionHeight = bottom - top;

        for (var oy = 0; oy < size; oy++)
        {
            var srcY0 = top + (int)((long)oy * regionHeight / size);
            var srcY1 = top + (int)((long)(oy + 1) * regionHeight / size);
            if (srcY1 <= srcY0) srcY1 = srcY0 + 1;
            if (srcY1 > bottom) srcY1 = bottom;

            for (var ox = 0; ox < size; ox++)
            {
                var srcX0 = left + (int)((long)ox * regionWidth / size);
                var srcX1 = left + (int)((long)(ox + 1) * regionWidth / size);
                if (srcX1 <= srcX0) srcX1 = srcX0 + 1;
                if (srcX1 > right) srcX1 = right;

                long total = 0;
                var count = 0;
                for (var y = srcY0; y < srcY1; y++)
                {
                    var rowStart = y * frame.Width;
                    for (var x = srcX0; x < srcX1; x++)
                    {
                        total += frame.Pixels[rowStart + x];
                        count++;
                    }
                }

                output[(oy * size) + ox] = count == 0 ? (byte)0 : (byte)(total / count);
            }
        }

        return output;
    }
}
