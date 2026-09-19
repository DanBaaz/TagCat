using System;
using System.Numerics;
using VideoDedupe.Models;
using VideoDedupe.Options;

namespace VideoDedupe.Hashing;

public interface IPerceptualHasher
{
    /// <summary>Reduces a frame to a 64-bit hash under the supplied options.</summary>
    ulong Hash(GrayFrame frame, DedupeOptions options);
}

/// <summary>
/// Classic DCT perceptual hash, implemented directly so the project carries no
/// imaging dependency. The frame is reduced to 32x32, transformed, and the
/// low-frequency 8x8 block is thresholded against its own median.
///
/// The DC term is deliberately excluded: it encodes overall brightness, which is
/// exactly the thing that shifts between re-encodes of the same content.
/// </summary>
public sealed class DctPerceptualHasher : IPerceptualHasher
{
    private const int SampleSize = 32;
    private const int LowFrequencySize = 8;

    /// <summary>
    /// Precomputed cosine basis. Shared and read-only after construction, so a
    /// single hasher instance is safe to use from every worker thread.
    /// </summary>
    private static readonly double[,] CosineTable = BuildCosineTable();

    public ulong Hash(GrayFrame frame, DedupeOptions options)
    {
        var pixels = FrameNormalizer.Normalize(frame, SampleSize, options);
        var dct = Transform2D(pixels);

        // Collect the low-frequency coefficients, skipping index 0,0.
        Span<double> coefficients = stackalloc double[LowFrequencySize * LowFrequencySize];
        var n = 0;
        for (var v = 0; v < LowFrequencySize; v++)
        {
            for (var u = 0; u < LowFrequencySize; u++)
            {
                if (u == 0 && v == 0) continue;
                coefficients[n++] = dct[(v * SampleSize) + u];
            }
        }

        var median = Median(coefficients[..n]);

        var hash = 0UL;
        for (var i = 0; i < n; i++)
        {
            if (coefficients[i] > median) hash |= 1UL << i;
        }

        return hash;
    }

    /// <summary>
    /// Separable 2D DCT-II. Rows are transformed first, then columns, which turns
    /// an O(n^4) direct evaluation into O(n^3) with identical output.
    /// </summary>
    private static double[] Transform2D(byte[] pixels)
    {
        var rows = new double[SampleSize * SampleSize];

        for (var y = 0; y < SampleSize; y++)
        {
            var rowStart = y * SampleSize;
            for (var u = 0; u < SampleSize; u++)
            {
                double sum = 0;
                for (var x = 0; x < SampleSize; x++)
                {
                    sum += pixels[rowStart + x] * CosineTable[x, u];
                }
                rows[rowStart + u] = sum * Alpha(u);
            }
        }

        var result = new double[SampleSize * SampleSize];

        for (var u = 0; u < SampleSize; u++)
        {
            for (var v = 0; v < SampleSize; v++)
            {
                double sum = 0;
                for (var y = 0; y < SampleSize; y++)
                {
                    sum += rows[(y * SampleSize) + u] * CosineTable[y, v];
                }
                result[(v * SampleSize) + u] = sum * Alpha(v);
            }
        }

        return result;
    }

    private static double Alpha(int index) =>
        index == 0 ? Math.Sqrt(1.0 / SampleSize) : Math.Sqrt(2.0 / SampleSize);

    private static double[,] BuildCosineTable()
    {
        var table = new double[SampleSize, SampleSize];
        for (var x = 0; x < SampleSize; x++)
        {
            for (var u = 0; u < SampleSize; u++)
            {
                table[x, u] = Math.Cos(((2 * x) + 1) * u * Math.PI / (2.0 * SampleSize));
            }
        }
        return table;
    }

    private static double Median(Span<double> values)
    {
        Span<double> copy = stackalloc double[values.Length];
        values.CopyTo(copy);
        copy.Sort();

        var mid = copy.Length / 2;
        return copy.Length % 2 == 0
            ? (copy[mid - 1] + copy[mid]) / 2.0
            : copy[mid];
    }
}

public static class HashUtils
{
    /// <summary>Number of differing bits between two hashes. 0 is identical, 64 is inverted.</summary>
    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Hamming distance expressed as a 0..1 similarity.</summary>
    public static double Similarity(ulong a, ulong b) => 1.0 - (HammingDistance(a, b) / 64.0);
}
