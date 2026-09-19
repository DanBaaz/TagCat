using System;
using System.IO;

namespace VideoDedupe;

/// <summary>
/// Recognises the folders that mean "deleted" on the platforms an external drive is
/// likely to have been plugged into. Shared between file discovery (which skips them
/// unless asked not to) and the results list (which flags anything found inside one),
/// so the two can never disagree about what counts as trash.
/// </summary>
public static class TrashPaths
{
    private static readonly string[] FolderNames =
    [
        "$Recycle.Bin",   // Windows, per-volume
        "RECYCLER",       // Windows, pre-Vista
        ".Trashes",       // macOS, on removable and network volumes
        ".Trash",         // macOS, per-user
        ".Trash-1000"     // Linux freedesktop, seen on shared drives
    ];

    /// <summary>
    /// True if any segment of the path is a trash folder. Every segment is checked rather
    /// than just the leaf, because the interesting case is a file buried several levels
    /// inside one, e.g. ...\.Trashes\501\Yellowstone\Season 1\ep.mp4.
    /// </summary>
    public static bool Contains(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var segments = path.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            foreach (var name in FolderNames)
            {
                if (segment.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// macOS writes a "._name.ext" companion beside every real file when it saves to a
    /// non-Mac-formatted volume, holding resource-fork metadata. These carry the video
    /// extension but contain no media, so every decoder rejects them with 0xC00D36C4.
    /// On a drive that has been used on a Mac there is roughly one per real file, which
    /// otherwise doubles the file count, halves the apparent success rate, and buries
    /// genuine decode failures in the log.
    /// </summary>
    public static bool IsAppleDoubleSidecar(string path) =>
        Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);
}
