using System;
using System.IO;

namespace MediaTagger.Services
{
    /// <summary>
    /// LibVLCSharp's own Core.Initialize() (no arguments) only checks the app's own folder.
    /// That's correct for the self-contained build, which bundles LibVLC directly - but the
    /// framework-dependent build deliberately does NOT bundle it, to keep the download small,
    /// and instead relies on either a VLC install already on the machine or a first-run
    /// installer that fetched just the LibVLC files. This checks all three locations, in the
    /// order a person is most likely to actually have them in.
    /// </summary>
    public static class LibVlcLocator
    {
        /// <summary>Null means LibVLC could not be found anywhere - video and audio preview
        /// will not be available, but the rest of the app (tagging, browsing) still can be.</summary>
        public static string? Find()
        {
            var candidates = new[]
            {
                // Where the installer places LibVLC if it had to fetch it itself, and where
                // the self-contained build already has it - checked first since it's the
                // fastest to confirm and the one most likely to be exactly the version tested.
                Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"),
                Path.Combine(AppContext.BaseDirectory, "libvlc"),
                AppContext.BaseDirectory,

                // A VLC the person already had installed - the installer skips fetching
                // anything if it finds one of these, and this is why it can.
                @"C:\Program Files\VideoLAN\VLC",
                @"C:\Program Files (x86)\VideoLAN\VLC"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(candidate, "libvlc.dll"))) return candidate;
                }
                catch
                {
                    // A malformed or inaccessible path here is not worth stopping over -
                    // just try the next candidate.
                }
            }

            return null;
        }
    }
}
