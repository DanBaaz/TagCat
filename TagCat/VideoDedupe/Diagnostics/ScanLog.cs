using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace VideoDedupe.Diagnostics;

/// <summary>What happened to one file during a scan.</summary>
public sealed record FileDiagnostic(
    string FilePath,
    bool Succeeded,
    TimeSpan Duration,
    int Width,
    int Height,
    int FramesRequested,
    int FramesDecoded,
    int FramesKept,
    string? Error)
{
    public string Summary => Succeeded
        ? $"OK   {Path.GetFileName(FilePath)}  {Width}x{Height}  {Duration:hh\\:mm\\:ss}  " +
          $"asked {FramesRequested}, decoded {FramesDecoded}, kept {FramesKept}"
        : $"FAIL {Path.GetFileName(FilePath)}  {Error}";
}

/// <summary>
/// Writes a plain-text record of every scan. When a scan finds nothing, the
/// question is always "did it actually read the files?", and without this there is
/// no way to answer it short of attaching a debugger.
/// </summary>
public sealed class ScanLog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoDedupe",
        "last-scan.log");

    public void Write(string line) =>
        _lines.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");

    public void Write(FileDiagnostic diagnostic) => Write(diagnostic.Summary);

    /// <summary>Flushes everything to disk and returns the path written to.</summary>
    public string Save()
    {
        var path = LogPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var builder = new StringBuilder();
        builder.AppendLine($"Video Duplicate Finder scan log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(new string('-', 70));

        while (_lines.TryDequeue(out var line)) builder.AppendLine(line);

        File.WriteAllText(path, builder.ToString());
        return path;
    }
}
