using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaTagger.Models;

namespace MediaTagger.Services
{
    public enum SortField { FileName, DateModified, DateCreated, Size, Random }
    public enum OrganizeBy { Year, YearMonth, SelectedTags, FirstLetterOfName }
    public enum FileOpMode { Move, Copy }

    public static class LibraryService
    {
        private static readonly string[] SupportedExt =
            MediaItem.ImageExtensions.Concat(MediaItem.VideoExtensions).Concat(MediaItem.AudioExtensions).ToArray();

        public static List<MediaItem> LoadFolder(string folderPath, bool includeSubfolders) =>
            LoadFolders(new[] { folderPath }, includeSubfolders);

        /// <summary>Loads files from multiple folders at once, de-duplicating any file that would
        /// otherwise be picked up twice (e.g. a parent folder and one of its own subfolders both selected
        /// while "include subfolders" is on).</summary>
        /// <param name="excludedSubfolders">Full paths to skip entirely, along with everything under
        /// them. Only meaningful when includeSubfolders is true; ignored otherwise since there is
        /// nothing recursive to exclude from.</param>
        public static List<MediaItem> LoadFolders(
            IEnumerable<string> folderPaths, bool includeSubfolders, IEnumerable<string>? excludedSubfolders = null,
            bool showHidden = false)
        {
            var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<MediaItem>();

            var excluded = (excludedSubfolders ?? Enumerable.Empty<string>())
                .Select(Path.GetFullPath)
                .ToList();

            foreach (var folderPath in folderPaths)
            {
                if (!Directory.Exists(folderPath)) continue;
                var files = Directory.EnumerateFiles(folderPath, "*.*", option)
                    .Where(f => SupportedExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Where(f => !IsAppleDoubleSidecar(f))
                    .Where(f => !IsUnderAnyExcludedFolder(f, excluded))
                    .Where(f => showHidden || !IsHidden(f, folderPath));

                foreach (var f in files)
                {
                    if (seen.Add(Path.GetFullPath(f)))
                        result.Add(new MediaItem(f));
                }
            }
            return result;
        }

        /// <summary>
        /// True if the file itself is hidden, or sits inside a hidden folder anywhere between
        /// it and the folder being scanned. Walking up matters because Windows does not mark
        /// the contents of a hidden folder as hidden - only the folder - so checking the file
        /// alone would still pull everything out of somewhere like a hidden .cache directory.
        /// The scanned folder itself is excluded from the walk: if someone explicitly points
        /// the app at a hidden folder, they clearly mean to see inside it.
        /// </summary>
        private static bool IsHidden(string filePath, string scanRoot)
        {
            try
            {
                if (File.GetAttributes(filePath).HasFlag(FileAttributes.Hidden)) return true;

                var root = Path.GetFullPath(scanRoot).TrimEnd(Path.DirectorySeparatorChar);
                var dir = Directory.GetParent(Path.GetFullPath(filePath));

                while (dir != null &&
                       !dir.FullName.TrimEnd(Path.DirectorySeparatorChar)
                           .Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    if (dir.Attributes.HasFlag(FileAttributes.Hidden)) return true;
                    dir = dir.Parent;
                }

                return false;
            }
            catch
            {
                // Unreadable attributes: treat as visible rather than silently dropping a file.
                return false;
            }
        }

        /// <summary>Checked against the file's full path rather than trying to skip the
        /// directory during enumeration - simpler, and the cost of still walking an excluded
        /// subtree is minor next to the size of most photo/video folders.</summary>
        private static bool IsUnderAnyExcludedFolder(string filePath, List<string> excludedFolders)
        {
            if (excludedFolders.Count == 0) return false;

            var fullPath = Path.GetFullPath(filePath);
            foreach (var excluded in excludedFolders)
            {
                if (fullPath.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(excluded + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// macOS writes a "._name.ext" companion beside every real file when saving to a
        /// non-Mac-formatted volume, holding resource-fork metadata. They carry the media
        /// extension but contain no media, so they appear as broken tiles with no thumbnail
        /// - and could be tagged by accident. On a drive that has seen a Mac there is
        /// roughly one per real file, so this quietly doubles a library otherwise.
        /// </summary>
        private static bool IsAppleDoubleSidecar(string path) =>
            Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);

        public static IEnumerable<MediaItem> Sort(IEnumerable<MediaItem> items, SortField field, bool ascending)
        {
            if (field == SortField.Random)
                return items.OrderBy(_ => Guid.NewGuid()); // reshuffles fresh on every call

            IOrderedEnumerable<MediaItem> ordered = field switch
            {
                SortField.DateModified => ascending
                    ? items.OrderBy(i => i.DateModified)
                    : items.OrderByDescending(i => i.DateModified),
                SortField.DateCreated => ascending
                    ? items.OrderBy(i => i.DateCreated)
                    : items.OrderByDescending(i => i.DateCreated),
                SortField.Size => ascending
                    ? items.OrderBy(i => i.SizeInBytes)
                    : items.OrderByDescending(i => i.SizeInBytes),
                _ => ascending
                    ? items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
                    : items.OrderByDescending(i => i.FileName, StringComparer.OrdinalIgnoreCase),
            };
            return ordered;
        }

        /// <summary>
        /// Moves each item into a destination subfolder under destRoot, based on the chosen scheme.
        /// Returns the number of files moved.
        /// </summary>
        public static int Organize(IEnumerable<MediaItem> items, string destRoot, OrganizeBy scheme, IReadOnlyList<string>? tagsForFolderName = null)
        {
            int moved = 0;
            foreach (var item in items.ToList())
            {
                string subfolder = scheme switch
                {
                    OrganizeBy.Year => item.DateModified.Year.ToString(),
                    OrganizeBy.YearMonth => item.DateModified.ToString("yyyy-MM"),
                    OrganizeBy.FirstLetterOfName => char.ToUpperInvariant(
                        item.BaseNameWithoutTags.Length > 0 ? item.BaseNameWithoutTags[0] : '_').ToString(),
                    OrganizeBy.SelectedTags => (tagsForFolderName != null && tagsForFolderName.Count > 0)
                        ? string.Join(" ", tagsForFolderName)
                        : "Untagged",
                    _ => "Sorted"
                };

                var destDir = Path.Combine(destRoot, SanitizeFolderName(subfolder));
                Directory.CreateDirectory(destDir);

                var destPath = Path.Combine(destDir, item.FileName);
                destPath = MakeUnique(destPath);

                if (!string.Equals(Path.GetFullPath(destPath), Path.GetFullPath(item.FullPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(item.FullPath, destPath);
                    moved++;
                }
            }
            return moved;
        }

        /// <summary>
        /// Moves or copies each item directly into destFolder (no subfolder grouping).
        /// Returns the number of files actually moved/copied.
        /// </summary>
        public static int CopyOrMove(IEnumerable<MediaItem> items, string destFolder, FileOpMode mode)
        {
            Directory.CreateDirectory(destFolder);

            int count = 0;
            foreach (var item in items.ToList())
            {
                var destPath = Path.Combine(destFolder, item.FileName);
                destPath = MakeUnique(destPath);

                if (string.Equals(Path.GetFullPath(destPath), Path.GetFullPath(item.FullPath), StringComparison.OrdinalIgnoreCase))
                    continue; // already there, nothing to do

                if (mode == FileOpMode.Move)
                    File.Move(item.FullPath, destPath);
                else
                    File.Copy(item.FullPath, destPath);
                count++;
            }
            return count;
        }

        private static string SanitizeFolderName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string MakeUnique(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int i = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                i++;
            } while (File.Exists(candidate));
            return candidate;
        }
    }
}
