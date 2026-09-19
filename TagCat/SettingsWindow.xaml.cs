using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaTagger.Models;
using MediaTagger.Services;

namespace MediaTagger
{
    /// <summary>
    /// Edits are staged: everything writes into the live Settings object as it's changed
    /// (so a slider or checkbox still previews immediately, the way it always has), but a
    /// full snapshot is taken when the window opens. Done persists what's live; Cancel - or
    /// closing the window any other way - restores that snapshot and reapplies it, so
    /// nothing changed in this dialog survives unless Done was actually clicked.
    ///
    /// Video preview (autoplay/loop/mute) is the one exception worth calling out: those
    /// three no longer touch the running session at all. They set what a *future* launch
    /// starts with, so toggling one here does not change the checkboxes under the preview
    /// panel right now - deliberately, since mixing "what happens right now" with "what
    /// happens next time" in the same control was confusing.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly AppSettings _original;
        private bool _ready;
        private bool _committed;

        public SettingsWindow(MainWindow owner, bool openedFromDuplicateFinder)
        {
            InitializeComponent();
            _owner = owner;
            _original = owner.Settings.Clone();

            var settings = _owner.Settings;

            ThumbnailSizeSlider.Value = _owner.ThumbnailHeight;

            // Read from the persisted default, not the live checkboxes - this dialog edits
            // what a future launch starts with, not what the current session is doing.
            AutoplayDefaultBox.IsChecked = settings.Autoplay;
            LoopDefaultBox.IsChecked = settings.Loop;
            MuteDefaultBox.IsChecked = settings.StartMuted;
            LimitPreviewToNativeBox.IsChecked = settings.LimitPreviewToNativeResolution;

            ShowHiddenFilesBox.IsChecked = settings.ShowHiddenFiles;

            if (settings.OpenFilesInViewer) OpenInViewerRadio.IsChecked = true;
            else OpenInSystemRadio.IsChecked = true;
            RetainFingerprintsBox.IsChecked = settings.RetainFingerprints;
            VirtualizationBox.IsChecked = settings.UseVirtualization;
            CloseDedupeOnExitBox.IsChecked = settings.CloseDuplicateFinderOnExit;

            if (settings.OpenDuplicateFinderAsPopup) DedupePopupRadio.IsChecked = true;
            else DedupeSeparateWindowRadio.IsChecked = true;

            if (settings.OpenDuplicatesInViewer) DupesInViewerRadio.IsChecked = true;
            else DupesInSystemRadio.IsChecked = true;
            DefaultFolderBox.Text = settings.DefaultFolder;

            if (settings.StartupMode == StartupFolderMode.PreviousFolders)
                StartPreviousFoldersRadio.IsChecked = true;
            else
                StartDefaultFolderRadio.IsChecked = true;

            ClearFingerprintsOnExitBox.IsChecked = settings.ClearFingerprintsOnExit;
            CacheFolderBox.Text = string.IsNullOrWhiteSpace(settings.FingerprintCacheFolder)
                ? AppSettings.DefaultFingerprintFolder
                : settings.FingerprintCacheFolder;

            RetainThumbnailsBox.IsChecked = settings.RetainThumbnails;
            ClearThumbnailsOnExitBox.IsChecked = settings.ClearThumbnailsOnExit;
            ThumbnailCacheFolderBox.Text = string.IsNullOrWhiteSpace(settings.ThumbnailCacheFolder)
                ? AppSettings.DefaultThumbnailCacheFolder
                : settings.ThumbnailCacheFolder;

            RefreshExcludedLists();

            // Null (never asked) shows as unticked; the startup prompt is what sets it either way.
            CheckUpdatesOnStartupBox.IsChecked = settings.CheckForUpdatesOnStartup == true;

            AboutHeadingText.Text = $"About TagCat v{MainWindow.CombinedVersion}";
            VersionText.Text = $"TagCat v{MainWindow.Version}";

            // No TagCat version here - the heading above already carries it, and repeating it
            // inside the Duplicate Finder's own entry read as though DF depended on it.
            DedupeVersionText.Text = $"Duplicate Finder v{VideoDedupe.DuplicateFinderWindow.DedupeVersion}";

            // Whichever app the gear was clicked in gets the benefit of the doubt about
            // what the person came here to change.
            MediaTaggerSection.IsExpanded = !openedFromDuplicateFinder;
            DuplicateFinderSection.IsExpanded = openedFromDuplicateFinder;

            _ready = true;

            UpdateThumbnailSizeText();
            RefreshCacheSize();
            RefreshThumbnailCacheSize();
        }

        /// <summary>The single source of truth, shared with the duplicate finder.</summary>
        private string FingerprintCachePath => _owner.Settings.FingerprintDatabasePath;

        /// <summary>The single source of truth, shared with the thumbnail loader.</summary>
        private string ThumbnailCachePath => _owner.Settings.ThumbnailDatabasePath;

        // ---------- Excluded from scanning ----------

        private void RefreshExcludedLists()
        {
            ExcludedFoldersList.ItemsSource = _owner.Settings.ExcludedScanFolders
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            ExcludedFilesList.ItemsSource = _owner.Settings.ExcludedScanFiles
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void AddExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose a folder to exclude from Duplicate Finder scans."
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var chosen = Path.GetFullPath(dialog.SelectedPath);
            if (_owner.Settings.ExcludedScanFolders.Contains(chosen, StringComparer.OrdinalIgnoreCase)) return;

            _owner.Settings.ExcludedScanFolders = _owner.Settings.ExcludedScanFolders.Append(chosen).ToArray();
            _owner.ApplyFingerprintRetentionToOpenFinder();
            RefreshExcludedLists();
        }

        private void RemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ExcludedFoldersList.SelectedItem is not string selected) return;

            _owner.Settings.ExcludedScanFolders = _owner.Settings.ExcludedScanFolders
                .Where(f => !string.Equals(f, selected, StringComparison.OrdinalIgnoreCase)).ToArray();
            _owner.ApplyFingerprintRetentionToOpenFinder();
            RefreshExcludedLists();
        }

        private void AddExcludedFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a file to exclude from Duplicate Finder scans.",
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;

            var existing = new HashSet<string>(_owner.Settings.ExcludedScanFiles, StringComparer.OrdinalIgnoreCase);
            var added = dialog.FileNames.Select(Path.GetFullPath).Where(existing.Add).ToList();
            if (added.Count == 0) return;

            _owner.Settings.ExcludedScanFiles = _owner.Settings.ExcludedScanFiles.Concat(added).ToArray();
            _owner.ApplyFingerprintRetentionToOpenFinder();
            RefreshExcludedLists();
        }

        private void RemoveExcludedFile_Click(object sender, RoutedEventArgs e)
        {
            if (ExcludedFilesList.SelectedItem is not string selected) return;

            _owner.Settings.ExcludedScanFiles = _owner.Settings.ExcludedScanFiles
                .Where(f => !string.Equals(f, selected, StringComparison.OrdinalIgnoreCase)).ToArray();
            _owner.ApplyFingerprintRetentionToOpenFinder();
            RefreshExcludedLists();
        }

        // ---------- Appearance ----------

        private void ThumbnailSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _owner.ThumbnailHeight = e.NewValue;
            _owner.Settings.ThumbnailHeight = e.NewValue;
            UpdateThumbnailSizeText();
        }

        /// <summary>Shows both dimensions, since the slider now scales the tile as a whole.</summary>
        private void UpdateThumbnailSizeText() =>
            ThumbnailSizeText.Text = $"{(int)_owner.TileWidth} × {(int)ThumbnailSizeSlider.Value}";

        private void ResetThumbnail_Click(object sender, RoutedEventArgs e) =>
            ThumbnailSizeSlider.Value = AppSettings.DefaultThumbnailHeight;

        private void Virtualization_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            _owner.Settings.UseVirtualization = VirtualizationBox.IsChecked == true;

            // Swapped immediately rather than on next launch, so the effect is visible
            // while the setting is still in front of the user. Reverted on Cancel.
            _owner.ApplyVirtualizationSetting();
        }

        private void ShowHiddenFiles_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            _owner.Settings.ShowHiddenFiles = ShowHiddenFilesBox.IsChecked == true;

            // Applied live rather than on next launch, since it changes what's in the grid
            // right now. Reverted along with everything else if Cancel is pressed.
            _owner.ReloadCurrentFolders();
        }

        private void OpenMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _owner.Settings.OpenFilesInViewer = OpenInViewerRadio.IsChecked == true;
        }

        // ---------- Startup folder ----------

        private void StartupMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            _owner.Settings.StartupMode = StartPreviousFoldersRadio.IsChecked == true
                ? StartupFolderMode.PreviousFolders
                : StartupFolderMode.DefaultFolder;
        }

        private void DefaultFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            _owner.Settings.DefaultFolder = DefaultFolderBox.Text?.Trim() ?? string.Empty;
        }

        private void BrowseDefaultFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose the folder TagCat opens on startup."
            };

            if (Directory.Exists(DefaultFolderBox.Text)) dialog.SelectedPath = DefaultFolderBox.Text;
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            DefaultFolderBox.Text = dialog.SelectedPath;

            // Choosing a default folder almost always means wanting to use it.
            StartDefaultFolderRadio.IsChecked = true;
        }

        // ---------- Duplicate finder options ----------

        private void DedupeOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            _owner.Settings.RetainFingerprints = RetainFingerprintsBox.IsChecked == true;
            _owner.Settings.CloseDuplicateFinderOnExit = CloseDedupeOnExitBox.IsChecked == true;
            _owner.Settings.OpenDuplicateFinderAsPopup = DedupePopupRadio.IsChecked == true;
            _owner.Settings.OpenDuplicatesInViewer = DupesInViewerRadio.IsChecked == true;
            _owner.Settings.ClearFingerprintsOnExit = ClearFingerprintsOnExitBox.IsChecked == true;

            // Push straight through to an already-open finder so the change is not
            // silently deferred until the window is next opened. Reverted on Cancel.
            _owner.ApplyFingerprintRetentionToOpenFinder();
        }

        // ---------- Video preview (startup defaults only - see class remarks) ----------

        private void PreviewDefault_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            _owner.Settings.Autoplay = AutoplayDefaultBox.IsChecked == true;
            _owner.Settings.Loop = LoopDefaultBox.IsChecked == true;
            _owner.Settings.StartMuted = MuteDefaultBox.IsChecked == true;
        }

        /// <summary>
        /// Unlike Autoplay/Loop/Mute above, this one is a live rendering preference rather
        /// than a startup default - it makes sense to see the effect immediately, the same
        /// way the thumbnail slider and virtualization toggle already do.
        /// </summary>
        private void LimitPreviewToNative_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            _owner.Settings.LimitPreviewToNativeResolution = LimitPreviewToNativeBox.IsChecked == true;
            _owner.UpdatePreviewHeight();
        }

        // ---------- Duplicate finder cache ----------

        private void CacheFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;

            var text = CacheFolderBox.Text?.Trim() ?? string.Empty;

            // Storing empty for the default keeps settings.json portable between machines
            // whose AppData paths differ.
            _owner.Settings.FingerprintCacheFolder =
                string.Equals(text, AppSettings.DefaultFingerprintFolder, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : text;

            _owner.ApplyFingerprintRetentionToOpenFinder();
            RefreshCacheSize();
        }

        private void BrowseCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose where the Duplicate Finder keeps its fingerprint cache."
            };

            if (Directory.Exists(CacheFolderBox.Text)) dialog.SelectedPath = CacheFolderBox.Text;
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            CacheFolderBox.Text = dialog.SelectedPath;
        }

        private void RefreshCacheSize()
        {
            try
            {
                var file = new FileInfo(FingerprintCachePath);
                if (!file.Exists)
                {
                    CacheSizeText.Text = "No cache in this folder yet — nothing has been scanned.";
                    ClearCacheButton.IsEnabled = false;
                    OpenCacheFolderButton.IsEnabled = false;
                    return;
                }

                CacheSizeText.Text = $"{Models.MediaItem.FormatSize(file.Length)} · last written {file.LastWriteTime:g}";
                ClearCacheButton.IsEnabled = true;
                OpenCacheFolderButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                CacheSizeText.Text = $"Couldn't read the cache: {ex.Message}";
                ClearCacheButton.IsEnabled = false;
            }
        }

        private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{FingerprintCachePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open the folder: {ex.Message}",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(this,
                "Delete the duplicate finder's fingerprint cache?\n\n" +
                "Nothing in your library is touched. The next scan just has to examine every " +
                "file from scratch, which takes longer.",
                "Clear cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                File.Delete(FingerprintCachePath);
                RefreshCacheSize();
            }
            catch (IOException)
            {
                // Almost always the duplicate finder holding the file open mid-scan.
                MessageBox.Show(this,
                    "The cache is in use. Close the Duplicate Finder window (or wait for the " +
                    "current scan to finish) and try again.",
                    "Clear cache", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't delete the cache: {ex.Message}",
                    "Clear cache", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- Thumbnail cache ----------

        private void ThumbnailCacheOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            _owner.Settings.RetainThumbnails = RetainThumbnailsBox.IsChecked == true;
            _owner.Settings.ClearThumbnailsOnExit = ClearThumbnailsOnExitBox.IsChecked == true;

            // Applies straight through to the running loader, same reasoning as the
            // fingerprint equivalent - and reverted along with everything else on Cancel.
            _owner.ApplyThumbnailCacheSetting();
            RefreshThumbnailCacheSize();
        }

        private void ThumbnailCacheFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;

            var text = ThumbnailCacheFolderBox.Text?.Trim() ?? string.Empty;

            _owner.Settings.ThumbnailCacheFolder =
                string.Equals(text, AppSettings.DefaultThumbnailCacheFolder, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : text;

            _owner.ApplyThumbnailCacheSetting();
            RefreshThumbnailCacheSize();
        }

        private void BrowseThumbnailCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose where TagCat keeps its thumbnail cache."
            };

            if (Directory.Exists(ThumbnailCacheFolderBox.Text)) dialog.SelectedPath = ThumbnailCacheFolderBox.Text;
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            ThumbnailCacheFolderBox.Text = dialog.SelectedPath;
        }

        private void RefreshThumbnailCacheSize()
        {
            try
            {
                var file = new FileInfo(ThumbnailCachePath);
                if (!file.Exists)
                {
                    ThumbnailCacheSizeText.Text = "No cache in this folder yet — nothing has been thumbnailed.";
                    ClearThumbnailCacheButton.IsEnabled = false;
                    OpenThumbnailCacheFolderButton.IsEnabled = false;
                    return;
                }

                ThumbnailCacheSizeText.Text = $"{Models.MediaItem.FormatSize(file.Length)} · last written {file.LastWriteTime:g}";
                ClearThumbnailCacheButton.IsEnabled = true;
                OpenThumbnailCacheFolderButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ThumbnailCacheSizeText.Text = $"Couldn't read the cache: {ex.Message}";
                ClearThumbnailCacheButton.IsEnabled = false;
            }
        }

        private void OpenThumbnailCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{ThumbnailCachePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open the folder: {ex.Message}",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearThumbnailCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(this,
                "Delete the thumbnail cache?\n\n" +
                "No files are touched. Thumbnails just get regenerated the next time they're needed, which takes longer.",
                "Clear cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                File.Delete(ThumbnailCachePath);
                RefreshThumbnailCacheSize();
            }
            catch (IOException)
            {
                MessageBox.Show(this,
                    "The cache is in use - a thumbnail may be mid-save. Try again in a moment.",
                    "Clear cache", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't delete the cache: {ex.Message}",
                    "Clear cache", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- About ----------

        private void Changelog_Click(object sender, RoutedEventArgs e) => _owner.ShowTagCatChangelog(this);

        // ---------- Licence ----------

        private void ViewLicence_Click(object sender, RoutedEventArgs e) => ShowTextFile("LICENSE", "Licence");

        private void ViewThirdParty_Click(object sender, RoutedEventArgs e) =>
            ShowTextFile("THIRD-PARTY-NOTICES.txt", "Third-party notices");

        /// <summary>
        /// Opens a text file shipped alongside the executable. Falls back to a message rather
        /// than an error if it isn't there - a missing licence file shouldn't look like a
        /// crash, and the same text is on the GitHub page anyway.
        /// </summary>
        private void ShowTextFile(string fileName, string title)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, fileName);
                if (!File.Exists(path))
                {
                    MessageBox.Show(this,
                        $"{fileName} isn't in the application folder. You can read it at " +
                        "https://github.com/DanBaaz/TagCat",
                        title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- Updates ----------

        private void UpdateOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _owner.Settings.CheckForUpdatesOnStartup = CheckUpdatesOnStartupBox.IsChecked == true;
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusText.Visibility = Visibility.Visible;
            UpdateStatusText.Text = "Checking...";

            try
            {
                var result = await UpdateChecker.CheckAsync(MainWindow.CombinedVersion);

                if (result.ErrorMessage != null)
                {
                    UpdateStatusText.Text = result.ErrorMessage;
                    return;
                }

                if (!result.UpdateAvailable)
                {
                    UpdateStatusText.Text = $"You're on the latest version (v{MainWindow.CombinedVersion}).";
                    return;
                }

                UpdateStatusText.Text = $"Version {result.LatestVersion} is available.";

                var answer = MessageBox.Show(this,
                    $"TagCat {result.LatestVersion} is available. You have v{MainWindow.CombinedVersion}.\n\n" +
                    "Open the download page?",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes)
                    OpenUrl(result.ReleaseUrl ?? UpdateChecker.ReleasesPageUrl);
            }
            finally
            {
                // In a finally so a failure can't leave the button permanently dead.
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not open the page",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void DedupeChangelog_Click(object sender, RoutedEventArgs e) => _owner.ShowDuplicateFinderChangelog(this);

        // ---------- Done / Cancel ----------

        private void Done_Click(object sender, RoutedEventArgs e)
        {
            _committed = true;
            _owner.Settings.Save();
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            RevertToSnapshot();
            _committed = true; // already reverted; OnClosed must not do it again
            Close();
        }

        /// <summary>
        /// Restores every field to how it was when the window opened, then reapplies the
        /// handful of settings that had already been pushed live (thumbnail size, the panel
        /// virtualization mode, and the fingerprint options an open Duplicate Finder may
        /// have already picked up). Settings not visibly applied - startup mode, the default
        /// folder, the video preview defaults - need no further action: restoring the
        /// object is enough, since nothing yet reads them until the next launch.
        /// </summary>
        private void RevertToSnapshot()
        {
            _owner.Settings = _original;
            _owner.ThumbnailHeight = _original.ThumbnailHeight;
            _owner.ApplyVirtualizationSetting();
            _owner.ApplyFingerprintRetentionToOpenFinder();
            _owner.ApplyThumbnailCacheSetting();
            _owner.UpdatePreviewHeight();
            _owner.ReloadCurrentFolders();
        }

        /// <summary>
        /// Closing via the window's own X (or Alt+F4) is neither Done nor Cancel, but has to
        /// resolve to one of them - and discarding is the safer default: someone who dismissed
        /// the window without an explicit "keep this" click most likely didn't mean to keep it.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            if (!_committed) RevertToSnapshot();
            base.OnClosed(e);
        }
    }
}
