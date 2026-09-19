using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoDedupe.Models;
namespace VideoDedupe.Extraction;

/// <summary>
/// Optional fallback extractor that shells out to an ffmpeg executable. Not wired
/// up by default.
///
/// Licensing note, since this matters if the app is sold: invoking ffmpeg.exe as a
/// separate process is not linking, so the GPL does not reach into your code. If
/// you ship the binary alongside your installer you are still redistributing it and
/// must honour its licence, which for the LGPL builds means providing the source
/// offer and the licence text. The cleanest option commercially is to not bundle it
/// at all: let the user point at their own ffmpeg in settings, and treat this
/// extractor as an optional power-user feature. That is why nothing here assumes a
/// bundled path.
///
/// Frames are read as raw grey8 over stdout, which avoids any image decoding.
/// </summary>
public sealed class FfmpegFrameExtractor : IFrameExtractor
{
    private const int DecodeWidth = 160;
    private const int DecodeHeight = 160;

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    private static readonly string[] Extensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".asf", ".3gp", ".webm",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".flv", ".ogv", ".rm", ".vob", ".divx"
    ];

    public FfmpegFrameExtractor(string ffmpegPath, string ffprobePath)
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _ffprobePath = ffprobePath ?? throw new ArgumentNullException(nameof(ffprobePath));
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public async Task<VideoMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var args = $"-v error -select_streams v:0 -show_entries stream=width,height -show_entries format=duration " +
                   $"-of default=noprint_wrappers=1 \"{path}\"";

        var output = await RunTextAsync(_ffprobePath, args, cancellationToken).ConfigureAwait(false);

        int width = 0, height = 0;
        double durationSeconds = 0;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length != 2) continue;

            switch (parts[0])
            {
                case "width": int.TryParse(parts[1], out width); break;
                case "height": int.TryParse(parts[1], out height); break;
                case "duration":
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds);
                    break;
            }
        }

        if (durationSeconds <= 0)
            throw new FrameExtractionException($"ffprobe reported no duration for '{path}'.");

        return new VideoMetadata(TimeSpan.FromSeconds(durationSeconds), width, height);
    }

    public async IAsyncEnumerable<GrayFrame> ExtractFramesAsync(
        string path,
        IReadOnlyList<TimeSpan> timestamps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var timestamp in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = await DecodeAtAsync(path, timestamp, cancellationToken).ConfigureAwait(false);
            if (frame is not null) yield return frame;
        }
    }

    private async Task<GrayFrame?> DecodeAtAsync(string path, TimeSpan timestamp, CancellationToken cancellationToken)
    {
        // -ss before -i performs a fast keyframe seek, which is exactly what we want:
        // frame-accurate seeking would be far slower and the hash does not need it.
        var seconds = timestamp.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var args = $"-ss {seconds} -i \"{path}\" -frames:v 1 " +
                   $"-vf scale={DecodeWidth}:{DecodeHeight} -pix_fmt gray -f rawvideo -v error pipe:1";

        var startInfo = new ProcessStartInfo(_ffmpegPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null) return null;

        using var memory = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(memory, cancellationToken);
        var drainErrors = process.StandardError.ReadToEndAsync(cancellationToken);

        await copyTask.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _ = await drainErrors.ConfigureAwait(false);

        var expected = DecodeWidth * DecodeHeight;
        var bytes = memory.ToArray();
        if (bytes.Length < expected) return null;

        return new GrayFrame(DecodeWidth, DecodeHeight, bytes, timestamp);
    }

    private static async Task<string> RunTextAsync(string exe, string args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo)
            ?? throw new FrameExtractionException($"Could not start '{exe}'.");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return stdout;
    }

    public void Dispose() { }
}
