using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaTagger.Models
{
    /// <summary>
    /// Handles the "basename [tag1 tag2 tag3]" filename convention.
    /// </summary>
    public static class TagParser
    {
        // Matches: <base>  [ <tags> ]   (tag block optional, trailing)
        private static readonly Regex Pattern =
            new(@"^(?<base>.*?)\s*(\[(?<tags>[^\[\]]*)\])?\s*$", RegexOptions.Compiled);

        public static (string BaseName, List<string> Tags) Parse(string nameWithoutExtension)
        {
            var match = Pattern.Match(nameWithoutExtension);
            var baseName = match.Groups["base"].Success ? match.Groups["base"].Value.Trim() : nameWithoutExtension;
            var tagsGroup = match.Groups["tags"];

            var tags = new List<string>();
            if (tagsGroup.Success && !string.IsNullOrWhiteSpace(tagsGroup.Value))
            {
                foreach (var t in tagsGroup.Value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                    tags.Add(t);
            }

            if (baseName.Length == 0) baseName = nameWithoutExtension;
            return (baseName, tags);
        }

        public static string BuildFileName(string baseName, List<string> tags, string extension)
        {
            if (tags.Count == 0)
                return $"{baseName}{extension}";
            return $"{baseName} [{string.Join(" ", tags)}]{extension}";
        }

        /// <summary>
        /// Characters that cannot appear in a Windows filename, plus the three the tag
        /// convention itself relies on: spaces separate tags, and square brackets delimit
        /// the tag block, so a tag containing any of them would corrupt the format and be
        /// parsed back differently from how it was written.
        /// </summary>
        private static readonly char[] ForbiddenInTag =
            Path.GetInvalidFileNameChars().Concat(new[] { '[', ']', ' ' }).Distinct().ToArray();

        /// <summary>
        /// True if the tag can be written to a filename and read back unchanged.
        /// </summary>
        public static bool IsValidTag(string tag) =>
            !string.IsNullOrWhiteSpace(tag) && tag.IndexOfAny(ForbiddenInTag) < 0;

        /// <summary>
        /// Strips characters a tag cannot contain, replacing runs of them with nothing.
        /// Returns an empty string if nothing usable is left, which callers treat as
        /// "drop this tag" rather than writing an empty one.
        /// </summary>
        public static string SanitizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;

            var builder = new StringBuilder(tag.Length);
            foreach (var c in tag)
            {
                if (Array.IndexOf(ForbiddenInTag, c) < 0) builder.Append(c);
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// Lists the offending characters in a tag, for telling the user precisely what is
        /// wrong rather than just refusing.
        /// </summary>
        public static string DescribeInvalidCharacters(string tag)
        {
            var bad = tag.Where(c => Array.IndexOf(ForbiddenInTag, c) >= 0)
                         .Distinct()
                         .Select(c => c == ' ' ? "space" : c.ToString())
                         .ToList();

            return string.Join(" ", bad);
        }
    }
}
