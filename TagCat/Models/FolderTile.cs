using System.IO;

namespace MediaTagger.Models
{
    /// <summary>One clickable folder tile in the thumbnail pane's navigation strip. Immutable
    /// and rebuilt fresh on every navigation, so it needs no change notification.</summary>
    public class FolderTile
    {
        public string FullPath { get; }
        public string Name { get; }

        public FolderTile(string fullPath)
        {
            FullPath = fullPath;

            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);

            // A drive root like "D:\" has no filename component; fall back to the full path
            // rather than showing an empty tile.
            Name = string.IsNullOrEmpty(name) ? fullPath : name;
        }
    }
}
