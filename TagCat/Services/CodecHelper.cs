using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MediaTagger.Services
{
    /// <summary>
    /// Some formats only decode if the matching Microsoft Store extension package is
    /// installed. Windows gives no useful error for this - a thumbnail request just comes
    /// back empty - so this maps the extensions that commonly fail to the package that
    /// fixes them, and remembers which have already been mentioned so the prompt appears
    /// once per session rather than once per file.
    ///
    /// Unlike the ffmpeg fallback in the Duplicate Finder, these are first-party Microsoft
    /// packages fetched from the Store by the Store itself - TagCat never downloads or
    /// redistributes anything, so there is no licensing consideration here at all.
    /// </summary>
    public static class CodecHelper
    {
        private sealed record CodecPackage(string DisplayName, string StoreProductId, string Note);

        private static readonly Dictionary<string, CodecPackage> Packages =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // HEIC needs BOTH HEIF Image Extensions and HEVC Video Extensions, because a
                // .heic file's image data is HEVC-compressed. Installing only the first is a
                // common dead end, so the note says so outright.
                [".heic"] = new("HEIF Image Extensions", "9PMMSR1CGPWG",
                    "HEIC files also need \"HEVC Video Extensions\" from the Store. Installing " +
                    "only HEIF Image Extensions is usually not enough."),
                [".heif"] = new("HEIF Image Extensions", "9PMMSR1CGPWG",
                    "HEIF files may also need \"HEVC Video Extensions\" from the Store."),
            };

        /// <summary>Extensions already mentioned this session, so the prompt does not repeat
        /// for every file in a folder full of them.</summary>
        private static readonly HashSet<string> Mentioned = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True if this file's type is one that needs a Store extension AND that type has not
        /// already been mentioned this session. Calling this marks it as mentioned, so the
        /// caller should only call it when it is actually about to show the prompt.
        /// </summary>
        public static bool ShouldSuggestCodecFor(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension)) return false;
            if (!Packages.ContainsKey(extension)) return false;

            return Mentioned.Add(extension);
        }

        public static string? DisplayNameFor(string filePath) =>
            Packages.TryGetValue(Path.GetExtension(filePath), out var package) ? package.DisplayName : null;

        public static string? NoteFor(string filePath) =>
            Packages.TryGetValue(Path.GetExtension(filePath), out var package) ? package.Note : null;

        /// <summary>
        /// Opens the Store straight to the relevant package. The ms-windows-store: protocol
        /// opens the Store app itself; the https apps.microsoft.com address is the fallback for
        /// machines where that protocol is unavailable (Store removed, or blocked by policy in
        /// a managed environment, which is common enough to be worth handling).
        /// </summary>
        public static void OpenStorePageFor(string filePath)
        {
            if (!Packages.TryGetValue(Path.GetExtension(filePath), out var package)) return;

            try
            {
                Process.Start(new ProcessStartInfo($"ms-windows-store://pdp/?ProductId={package.StoreProductId}")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo($"https://apps.microsoft.com/detail/{package.StoreProductId}")
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Nothing more to try. The prompt has already told them the package name,
                    // which is enough to find it by hand.
                }
            }
        }
    }
}
