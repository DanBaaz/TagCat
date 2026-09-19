using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace MediaTagger.Models
{
    public enum MediaKind { Image, Video, Audio, Other }

    /// <summary>
    /// Represents one file on disk plus the tags parsed out of its filename.
    /// Filename convention: "basename [tag1 tag2 tag3].ext"
    /// </summary>
    public class MediaItem : INotifyPropertyChanged
    {
        public string FullPath { get; private set; }
        public string Directory => Path.GetDirectoryName(FullPath) ?? "";
        public string FileName => Path.GetFileName(FullPath);
        public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();
        public string BaseNameWithoutTags { get; private set; } = "";
        public List<string> Tags { get; private set; } = new();
        public DateTime DateModified { get; private set; }
        public DateTime DateCreated { get; private set; }
        public long SizeInBytes { get; private set; }
        public MediaKind Kind { get; private set; }

        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(nameof(Thumbnail)); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public string TagsDisplay => Tags.Count > 0 ? string.Join(" ", Tags) : "(no tags)";

        public string SizeDisplay => FormatSize(SizeInBytes);

        public static string FormatSize(long bytes)
        {
            double size = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.#} {units[unitIndex]}";
        }

        public static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".heic" };
        public static readonly string[] VideoExtensions =
            { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm" };
        public static readonly string[] AudioExtensions =
            { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a" };

        private static readonly HashSet<string> ImageExt = new(ImageExtensions, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> VideoExt = new(VideoExtensions, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AudioExt = new(AudioExtensions, StringComparer.OrdinalIgnoreCase);

        public MediaItem(string fullPath)
        {
            FullPath = fullPath;
            Refresh();
        }

        /// <summary>Re-reads filename/tags/dates from disk (call after a rename or on load).</summary>
        public void Refresh()
        {
            var name = Path.GetFileNameWithoutExtension(FullPath);
            var ext = Extension;

            var (baseName, tags) = TagParser.Parse(name);
            BaseNameWithoutTags = baseName;
            Tags = tags;

            if (ImageExt.Contains(ext)) Kind = MediaKind.Image;
            else if (VideoExt.Contains(ext)) Kind = MediaKind.Video;
            else if (AudioExt.Contains(ext)) Kind = MediaKind.Audio;
            else Kind = MediaKind.Other;

            try
            {
                var fi = new FileInfo(FullPath);
                DateModified = fi.LastWriteTime;
                DateCreated = fi.CreationTime;
                SizeInBytes = fi.Length;
            }
            catch
            {
                DateModified = DateTime.MinValue;
                DateCreated = DateTime.MinValue;
                SizeInBytes = 0;
            }

            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(TagsDisplay));
            OnPropertyChanged(nameof(Tags));
        }

        /// <summary>
        /// Renames the underlying file on disk so its tags match the given list.
        /// Returns the new full path on success.
        /// </summary>
        /// <summary>Windows refuses paths at or beyond this length unless long-path support is
        /// enabled, which cannot be relied on. Tags lengthen filenames, so this is reachable.</summary>
        private const int MaxPathLength = 259;

        /// <summary>
        /// Applies a tag set by renaming the file. Returns what happened, so callers can
        /// report a collision or a rejected rename rather than the change appearing to
        /// succeed silently.
        /// </summary>
        public TagApplyResult ApplyTags(IEnumerable<string> newTags)
        {
            var cleanTags = newTags
                .Select(TagParser.SanitizeTag)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var newFileName = TagParser.BuildFileName(BaseNameWithoutTags, cleanTags, Extension);
            var newFullPath = Path.Combine(Directory, newFileName);

            if (string.Equals(newFullPath, FullPath, StringComparison.OrdinalIgnoreCase))
            {
                Refresh();
                return TagApplyResult.Unchanged(FullPath);
            }

            // Checked before touching the disk: File.Move would throw part-way through a
            // bulk operation, leaving some files renamed and some not.
            if (newFullPath.Length > MaxPathLength)
            {
                return TagApplyResult.Failed(FullPath,
                    $"the resulting name would be {newFullPath.Length} characters, over the {MaxPathLength} Windows allows. " +
                    "Use shorter tags, or move the file to a folder with a shorter path.");
            }

            var uniquePath = MakeUnique(newFullPath);
            bool renamedToAvoidCollision =
                !string.Equals(uniquePath, newFullPath, StringComparison.OrdinalIgnoreCase);

            var previousPath = FullPath;

            try
            {
                File.Move(FullPath, uniquePath);
            }
            catch (Exception ex)
            {
                return TagApplyResult.Failed(FullPath, ex.Message);
            }

            FullPath = uniquePath;
            Refresh();

            return renamedToAvoidCollision
                ? TagApplyResult.Collided(previousPath, FullPath)
                : TagApplyResult.Renamed(previousPath, FullPath);
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

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
