using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaTagger.Models
{
    /// <summary>What TagCat should open when it starts.</summary>
    public enum StartupFolderMode
    {
        /// <summary>Open the default folder configured below.</summary>
        DefaultFolder,

        /// <summary>Reopen whatever folders were loaded when the app last closed.</summary>
        PreviousFolders
    }

    /// <summary>
    /// User settings, persisted as JSON next to the app's other per-user data. Kept
    /// deliberately small and forgiving: a corrupt or partial file falls back to defaults
    /// rather than blocking startup, because losing preferences is a nuisance and failing
    /// to launch is not.
    /// </summary>
    public class AppSettings
    {
        /// <summary>The tile height the app ships with, shown in Settings so the slider
        /// can be put back without guessing.</summary>
        public const double DefaultThumbnailHeight = 110;

        public double ThumbnailHeight { get; set; } = DefaultThumbnailHeight;

        public bool Autoplay { get; set; } = true;
        public bool Loop { get; set; } = true;
        public bool StartMuted { get; set; }

        /// <summary>
        /// Never lets the preview render a video or image bigger than its actual pixel size.
        /// Off by default: growing the preview to fill the pane is usually what's wanted, and
        /// this trades that off against avoiding the soft, blurry look of a small image
        /// stretched past its native resolution.
        /// </summary>
        public bool LimitPreviewToNativeResolution { get; set; }

        /// <summary>
        /// Show files and folders Windows marks as hidden. Off by default, matching Explorer:
        /// hidden items are usually system clutter rather than anything worth tagging.
        /// </summary>
        public bool ShowHiddenFiles { get; set; }

        /// <summary>
        /// True (the default) opens files in TagCat's own viewer popup; false hands them
        /// to whatever Windows has associated with the file type. Applies to double-click and
        /// Enter; the right-click menu offers both regardless.
        /// </summary>
        public bool OpenFilesInViewer { get; set; } = true;

        /// <summary>Saved folders shown in the Select Folder dropdown, for jumping straight
        /// back to places you work in often.</summary>
        public string[] BookmarkedFolders { get; set; } = Array.Empty<string>();

        /// <summary>The persisted tag library: a flat vocabulary of tags kept independently of
        /// any folder, so autocomplete can suggest tags you use across your whole collection,
        /// not just ones already present in whatever's currently loaded.</summary>
        public string[] TagLibrary { get; set; } = Array.Empty<string>();

        public bool AutocompleteFromFolder { get; set; } = true;
        public bool AutocompleteFromLibrary { get; set; } = true;

        /// <summary>
        /// When false the duplicate finder neither reads nor writes its fingerprint cache,
        /// so every scan starts from scratch and nothing is left on disk afterwards.
        /// </summary>
        public bool RetainFingerprints { get; set; } = true;

        /// <summary>
        /// Remembered only for the "reopen previous folders" startup mode - opening the
        /// default folder always starts from the plain default (subfolders on, every type
        /// shown), since that folder may have nothing to do with what these described last.
        /// </summary>
        public bool IncludeSubfolders { get; set; } = true;

        /// <summary>Extensions unticked in "Filter by type" when the app last closed, restored
        /// alongside IncludeSubfolders under the same "reopen previous folders" condition.</summary>
        public string[] UncheckedExtensions { get; set; } = Array.Empty<string>();

        /// <summary>Delete the fingerprint cache when the app closes.</summary>
        public bool ClearFingerprintsOnExit { get; set; }

        /// <summary>
        /// Where the duplicate finder keeps its fingerprint cache. Configurable because the
        /// cache grows with the size of the library scanned, and a large one is better placed
        /// on a roomy drive than on the system disk. Empty means the default location.
        /// </summary>
        public string FingerprintCacheFolder { get; set; } = string.Empty;

        /// <summary>
        /// When true only the tiles on screen are built, so a large library stays fast.
        /// Off falls back to building every tile up front, which is simpler and avoids any
        /// virtualization quirk at the cost of memory and load time.
        /// </summary>
        public bool UseVirtualization { get; set; } = true;

        /// <summary>Close the Duplicate Finder window when TagCat closes.</summary>
        public bool CloseDuplicateFinderOnExit { get; set; } = true;

        /// <summary>
        /// True opens the Duplicate Finder as a modal popup owned by TagCat - it stays
        /// in front and blocks interaction behind it. False (the default) opens it as an
        /// independent window you can sit alongside TagCat and tag files in front of,
        /// which matters because a scan can run for a long time.
        /// </summary>
        public bool OpenDuplicateFinderAsPopup { get; set; }

        /// <summary>
        /// True (the default) plays a duplicate in TagCat's own viewer, where scrolling
        /// moves between the other files in that same duplicate group - handy for comparing
        /// them. False hands the file to whatever Windows has associated with it.
        /// </summary>
        public bool OpenDuplicatesInViewer { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StartupFolderMode StartupMode { get; set; } = StartupFolderMode.DefaultFolder;

        /// <summary>Used when StartupMode is DefaultFolder. Empty means open nothing.</summary>
        public string DefaultFolder { get; set; } =
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        /// <summary>Written on exit so PreviousFolders has something to restore.</summary>
        public string[] LastFolders { get; set; } = Array.Empty<string>();

        // ---------- Thumbnail cache ----------

        /// <summary>
        /// Keep generated thumbnails on disk so reopening a folder doesn't mean regenerating
        /// every one from scratch. On by default, mirroring the Duplicate Finder's fingerprint
        /// cache in both behaviour and how it is stored.
        /// </summary>
        public bool RetainThumbnails { get; set; } = true;

        public bool ClearThumbnailsOnExit { get; set; }

        /// <summary>Empty means the default location.</summary>
        public string ThumbnailCacheFolder { get; set; } = string.Empty;

        public static string DefaultThumbnailCacheFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TagCat");

        public string ThumbnailDatabasePath => Path.Combine(
            string.IsNullOrWhiteSpace(ThumbnailCacheFolder)
                ? DefaultThumbnailCacheFolder
                : ThumbnailCacheFolder,
            "thumbnails.db");

        // ---------- Duplicate Finder: excluded from scanning ----------

        /// <summary>Folders skipped by every scan unless a scan opts back in. Two distinct
        /// lists (folders, files) rather than one mixed list, since a folder exclusion means
        /// "everything under here" while a file exclusion means exactly one file.</summary>
        public string[] ExcludedScanFolders { get; set; } = Array.Empty<string>();

        public string[] ExcludedScanFiles { get; set; } = Array.Empty<string>();

        // ---------- Updates ----------

        /// <summary>
        /// Whether to check GitHub for a newer version at startup. Null means "not asked yet"
        /// rather than false, so the first run can ask once instead of quietly contacting a
        /// server nobody agreed to.
        /// </summary>
        public bool? CheckForUpdatesOnStartup { get; set; }

        /// <summary>Suppresses repeat prompts for a version already declined.</summary>
        public string SkippedUpdateVersion { get; set; } = string.Empty;

        /// <summary>Where the cache lives when no folder has been chosen.</summary>
        public static string DefaultFingerprintFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TagCat");

        /// <summary>
        /// Full path to the cache file. Single source of truth: this used to be worked out
        /// separately in the duplicate finder and in the settings dialog, so a change in one
        /// would have left the other pointing at a file nothing was writing.
        /// </summary>
        public string FingerprintDatabasePath => Path.Combine(
            string.IsNullOrWhiteSpace(FingerprintCacheFolder)
                ? DefaultFingerprintFolder
                : FingerprintCacheFolder,
            "fingerprints.db");

        // ---------- Persistence ----------

        /// <summary>
        /// A snapshot independent of this instance, taken when the Settings window opens so
        /// Cancel has something to restore. A shallow copy would share the array fields, and
        /// mutating one through the live object would corrupt the "original" it was meant to
        /// preserve, so LastFolders and UncheckedExtensions are copied explicitly.
        /// </summary>
        public AppSettings Clone() => new()
        {
            ThumbnailHeight = ThumbnailHeight,
            Autoplay = Autoplay,
            Loop = Loop,
            StartMuted = StartMuted,
            LimitPreviewToNativeResolution = LimitPreviewToNativeResolution,
            ShowHiddenFiles = ShowHiddenFiles,
            OpenFilesInViewer = OpenFilesInViewer,
            BookmarkedFolders = (string[])BookmarkedFolders.Clone(),
            TagLibrary = (string[])TagLibrary.Clone(),
            AutocompleteFromFolder = AutocompleteFromFolder,
            AutocompleteFromLibrary = AutocompleteFromLibrary,
            RetainFingerprints = RetainFingerprints,
            IncludeSubfolders = IncludeSubfolders,
            UncheckedExtensions = (string[])UncheckedExtensions.Clone(),
            ClearFingerprintsOnExit = ClearFingerprintsOnExit,
            FingerprintCacheFolder = FingerprintCacheFolder,
            CloseDuplicateFinderOnExit = CloseDuplicateFinderOnExit,
            OpenDuplicateFinderAsPopup = OpenDuplicateFinderAsPopup,
            OpenDuplicatesInViewer = OpenDuplicatesInViewer,
            StartupMode = StartupMode,
            DefaultFolder = DefaultFolder,
            LastFolders = (string[])LastFolders.Clone(),
            RetainThumbnails = RetainThumbnails,
            ClearThumbnailsOnExit = ClearThumbnailsOnExit,
            ThumbnailCacheFolder = ThumbnailCacheFolder,
            ExcludedScanFolders = (string[])ExcludedScanFolders.Clone(),
            ExcludedScanFiles = (string[])ExcludedScanFiles.Clone(),
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
            SkippedUpdateVersion = SkippedUpdateVersion
        };

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TagCat",
            "settings.json");

        /// <summary>Where settings lived when this app was called TagCat.</summary>
        private static string LegacySettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MediaTagger",
            "settings.json");

        /// <summary>
        /// One-time move of settings from the old TagCat folder to the TagCat one.
        /// Deliberately a copy, not a move: if anything goes wrong the original is still
        /// sitting there untouched, and a stray leftover folder is a far smaller problem
        /// than losing someone's tag library and bookmarks.
        ///
        /// Only settings.json is carried over. The thumbnail and fingerprint caches are
        /// left behind on purpose - they rebuild themselves on demand, they can be large,
        /// and they may be locked by another process, so moving them would be all risk for
        /// no benefit.
        /// </summary>
        private static void MigrateLegacySettingsIfNeeded()
        {
            try
            {
                if (File.Exists(SettingsPath)) return;          // already migrated, or a fresh install
                if (!File.Exists(LegacySettingsPath)) return;   // nothing to migrate

                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.Copy(LegacySettingsPath, SettingsPath, overwrite: false);
            }
            catch
            {
                // A failed migration just means starting from defaults, which is recoverable.
                // Throwing here would stop the app launching at all, which is not.
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static AppSettings Load()
        {
            MigrateLegacySettingsIfNeeded();

            try
            {
                if (!File.Exists(SettingsPath)) return new AppSettings();

                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                // Unreadable or malformed: defaults are always better than a failed start.
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                // Saving preferences is never worth interrupting the user over.
            }
        }
    }
}
