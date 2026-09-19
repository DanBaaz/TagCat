using System;
using System.Text.RegularExpressions;

namespace MediaTagger.Services
{
    /// <summary>
    /// Compares TagCat version strings. This can't use System.Version: the format is
    /// "0.&lt;TagCat&gt;.&lt;DF&gt;" where either part may carry a letter suffix, and Version.Parse
    /// throws on letters outright.
    ///
    /// The rule for letters is that a lettered release is a FIX on top of its base number, so
    /// 0.58a is NEWER than 0.58, and 0.58b newer than 0.58a. Letters are compared by length
    /// first and then alphabetically, so a hypothetical "aa" after "z" still sorts correctly -
    /// plain alphabetical would put "aa" before "b", which would be wrong.
    /// </summary>
    public static class VersionComparer
    {
        private sealed record Part(int Number, string Letters);

        /// <summary>
        /// Returns &gt;0 if candidate is newer than current, 0 if equal, &lt;0 if older.
        /// Returns null when either string can't be parsed - the caller should treat that as
        /// "don't know", not as "no update". Silently deciding a malformed tag means "older"
        /// is how an update checker quietly stops working forever.
        /// </summary>
        public static int? Compare(string candidate, string current)
        {
            var a = Parse(candidate);
            var b = Parse(current);
            if (a == null || b == null) return null;

            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                // A missing trailing part counts as 0, so "0.58" and "0.58.0" compare equal.
                var left = i < a.Length ? a[i] : new Part(0, "");
                var right = i < b.Length ? b[i] : new Part(0, "");

                int numeric = left.Number.CompareTo(right.Number);
                if (numeric != 0) return numeric;

                int letters = CompareLetters(left.Letters, right.Letters);
                if (letters != 0) return letters;
            }

            return 0;
        }

        /// <summary>No letters sorts before any letters, since the lettered build came after.</summary>
        private static int CompareLetters(string left, string right)
        {
            if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Accepts "0.58.19", "v0.58.19", "0.58a.19", "0.58" and so on. A leading "v" is
        /// tolerated because that's the conventional form for a git tag.
        /// </summary>
        private static Part[]? Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;

            var text = version.Trim();

            // Release tags in practice carry a name prefix - the repo's own first tag is
            // "TagCat-v0.58.19", not "v0.58.19". Anything before the first digit is stripped
            // rather than matching one specific prefix, so renaming the scheme later doesn't
            // silently stop the update check from ever finding a release again.
            var firstDigit = Regex.Match(text, @"\d");
            if (!firstDigit.Success) return null;
            text = text[firstDigit.Index..];

            var segments = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            var parts = new Part[segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                var match = Regex.Match(segments[i].Trim(), @"^(\d+)([A-Za-z]*)$");
                if (!match.Success) return null;

                parts[i] = new Part(int.Parse(match.Groups[1].Value), match.Groups[2].Value);
            }

            return parts;
        }
    }
}
