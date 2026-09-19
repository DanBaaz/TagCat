using System;

namespace VideoDedupe.Models;

/// <summary>
/// A single decoded video frame reduced to 8-bit greyscale.
/// This is the only currency passed between the extraction layer and the hashing
/// layer, which keeps the hashers completely free of any decoder dependency.
/// </summary>
public sealed class GrayFrame
{
    public GrayFrame(int width, int height, byte[] pixels, TimeSpan timestamp)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length < width * height)
            throw new ArgumentException("Pixel buffer is smaller than width * height.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
        Timestamp = timestamp;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major luminance values, one byte per pixel.</summary>
    public byte[] Pixels { get; }

    /// <summary>Position of this frame within the source video.</summary>
    public TimeSpan Timestamp { get; }

    public byte this[int x, int y] => Pixels[(y * Width) + x];

    /// <summary>
    /// Builds a greyscale frame from a packed BGRA buffer (the layout returned by
    /// both WIC and the Windows.Graphics.Imaging decoders).
    /// </summary>
    public static GrayFrame FromBgra32(ReadOnlySpan<byte> bgra, int width, int height, int stride, TimeSpan timestamp)
    {
        var gray = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * stride;
            var outStart = y * width;

            for (var x = 0; x < width; x++)
            {
                var i = rowStart + (x * 4);
                // Rec. 601 luma weights, integer maths to avoid float cost per pixel.
                var luma = ((bgra[i + 2] * 299) + (bgra[i + 1] * 587) + (bgra[i] * 114)) / 1000;
                gray[outStart + x] = (byte)luma;
            }
        }

        return new GrayFrame(width, height, gray, timestamp);
    }

    /// <summary>Mean luminance across the whole frame. Used for blank-frame rejection.</summary>
    public double MeanLuminance()
    {
        long total = 0;
        var count = Width * Height;
        for (var i = 0; i < count; i++) total += Pixels[i];
        return (double)total / count;
    }

    /// <summary>
    /// Standard deviation of luminance. Frames with almost no variance are fades,
    /// black frames or solid title cards, which match everything and must be dropped.
    /// </summary>
    public double LuminanceStdDev()
    {
        var mean = MeanLuminance();
        double sumSq = 0;
        var count = Width * Height;
        for (var i = 0; i < count; i++)
        {
            var d = Pixels[i] - mean;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / count);
    }
}
