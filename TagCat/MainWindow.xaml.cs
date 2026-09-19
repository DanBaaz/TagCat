using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaTagger.Models;
using MediaTagger.Services;

namespace MediaTagger
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const string AppVersion = "0.59b";

        private List<MediaItem> _allItems = new();
        private readonly ObservableCollection<MediaItem> _displayedItems = new();
        private readonly ObservableCollection<TagOption> _tagOptions = new();
        private readonly ObservableCollection<ExcludeTagOption> _excludeTagOptions = new();
        private readonly ObservableCollection<ExtensionFilterOption> _imageExtensionOptions = new();
        private readonly ObservableCollection<ExtensionFilterOption> _videoExtensionOptions = new();
        private readonly ObservableCollection<ExtensionFilterOption> _audioExtensionOptions = new();
        private readonly ObservableCollection<string> _excludedSubfolderPaths = new();
        private readonly ObservableCollection<BookmarkRow> _bookmarkedFolders = new();
        private readonly ObservableCollection<FolderTile> _folderTiles = new();

        /// <summary>Non-null while "Show Folders in Thumbnail Pane" is active: the single
        /// folder currently being browsed one level at a time. Null means the app is in its
        /// normal flat, possibly-multi-folder mode.</summary>
        private string? _browsingFolder;
        private readonly ObservableCollection<RemoveTagOption> _removeTagOptions = new();
        private MediaItem? _selectedItem;
        private List<string> _currentFolders = new();
        private bool _suppressEvents;
        private bool _initialized;
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private bool _isMuted = false;

        private readonly ThumbnailLoader _thumbnailLoader;
        private ThumbnailCacheStore? _thumbnailCache;

        /// <summary>Loaded once at startup and written back on exit.</summary>
        internal AppSettings Settings { get; set; } = new();
        private int _lastVolume = 100;
        private DispatcherTimer? _positionTimer;
        private bool _isDraggingSlider;
        private int _currentNativeWidth;
        private int _currentNativeHeight;
        private bool _previewExpandedToNative;
        private int _previewRequestId;

        public MainWindow()
        {
            InitializeComponent();
            MediaItemsControl.ItemsSource = _displayedItems;
            TagFilterList.ItemsSource = _tagOptions;
            ExcludeTagList.ItemsSource = _excludeTagOptions;
            _thumbnailLoader = new ThumbnailLoader(Dispatcher);
            _thumbnailLoader.CodecSuggested += OnCodecSuggested;

            ImageExtensionList.ItemsSource = _imageExtensionOptions;
            VideoExtensionList.ItemsSource = _videoExtensionOptions;
            AudioExtensionList.ItemsSource = _audioExtensionOptions;
            ExcludedSubfoldersList.ItemsSource = _excludedSubfolderPaths;
            BookmarkList.ItemsSource = _bookmarkedFolders;
            FolderTileList.ItemsSource = _folderTiles;
            RemoveTagList.ItemsSource = _removeTagOptions;

            // Core.Initialize() with no path only ever looks in the app's own folder, which
            // is right for the self-contained build (LibVLC is bundled right there) but wrong
            // for the framework-dependent one, which deliberately doesn't bundle it - this
            // also checks for a VLC already on the machine, or one an installer fetched.
            //
            // Unlike the media viewer, this window's ~27 other _mediaPlayer call sites were
            // never written expecting it to be null - none guard against it. Continuing
            // anyway would trade one clear message now for a scattered, confusing crash later
            // at some unpredictable point in the session, so this fails clearly up front
            // instead.
            var libVlcPath = MediaTagger.Services.LibVlcLocator.Find();
            try
            {
                if (libVlcPath != null) Core.Initialize(libVlcPath); else Core.Initialize();
                _libVLC = new LibVLC();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "TagCat could not find VLC, which it needs to run.\n\n" +
                    "If you installed this with Install.bat, try running it again - it downloads " +
                    "what's missing. Otherwise, installing VLC (videolan.org) and restarting " +
                    "TagCat will fix this.\n\n" +
                    $"Details: {ex.Message}",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
                return;
            }
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.EndReached += MediaPlayer_EndReached;
            PreviewVideo.MediaPlayer = _mediaPlayer;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            _initialized = true;
            RefreshDisplayList();
            UpdateFolderOptionsEnabledState();

            ApplySettingsOnStartup();

            // Deferred to Loaded so a slow or unreachable network can never delay the window
            // appearing - the check is entirely optional and nothing waits on it.
            Loaded += async (_, _) => await MaybeCheckForUpdatesAsync();
        }

        /// <summary>
        /// Startup update check. Asks once before ever contacting GitHub, rather than
        /// defaulting to on - a file-management app quietly phoning out on first launch is
        /// not a reasonable default, even for something as harmless as a version number.
        /// </summary>
        private async Task MaybeCheckForUpdatesAsync()
        {
            if (Settings.CheckForUpdatesOnStartup == null)
            {
                var answer = MessageBox.Show(this,
                    "Would you like TagCat to check for updates when it starts?\n\n" +
                    "This asks GitHub for the latest version number and nothing else. Nothing " +
                    "is downloaded or installed automatically, and you can change this later " +
                    "in Settings.",
                    "Check for updates?", MessageBoxButton.YesNo, MessageBoxImage.Question);

                Settings.CheckForUpdatesOnStartup = answer == MessageBoxResult.Yes;
                SaveSettings();

                if (Settings.CheckForUpdatesOnStartup != true) return;
            }
            else if (Settings.CheckForUpdatesOnStartup != true)
            {
                return;
            }

            var result = await UpdateChecker.CheckAsync(AppVersion);

            // A failed check at startup stays silent. Someone who did not ask about updates
            // this session should not get an error dialog about one; the Settings button
            // reports failures properly for anyone who actually went looking.
            if (result.ErrorMessage != null || !result.UpdateAvailable) return;

            // Respects a version previously declined, so saying no means no until the next one.
            if (string.Equals(result.LatestVersion, Settings.SkippedUpdateVersion,
                    StringComparison.OrdinalIgnoreCase)) return;

            var choice = MessageBox.Show(this,
                $"TagCat {result.LatestVersion} is available. You have v{AppVersion}.\n\n" +
                "Open the download page?\n\n" +
                "Choosing No won't ask again for this version.",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (choice == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(result.ReleaseUrl ?? UpdateChecker.ReleasesPageUrl)
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Nothing useful to say - the release page is public and findable by hand.
                }
            }
            else
            {
                Settings.SkippedUpdateVersion = result.LatestVersion ?? "";
                SaveSettings();
            }
        }

        private async void ApplySettingsOnStartup()
        {
            Settings = AppSettings.Load();

            ThumbnailHeight = Settings.ThumbnailHeight;
            ApplyThumbnailCacheSetting();
            _bookmarkedFolders.Clear();
            foreach (var folder in Settings.BookmarkedFolders) _bookmarkedFolders.Add(new BookmarkRow(folder));
            UpdateBookmarkEmptyState();

            AutocompleteFromFolderBox.IsChecked = Settings.AutocompleteFromFolder;
            AutocompleteFromLibraryBox.IsChecked = Settings.AutocompleteFromLibrary;
            ApplyVirtualizationSetting();
            AutoplayCheckBox.IsChecked = Settings.Autoplay;
            LoopCheckBox.IsChecked = Settings.Loop;
            _isMuted = Settings.StartMuted;
            _lastVolume = 100;
            ApplyMuteState();

            bool reopeningPrevious = Settings.StartupMode == StartupFolderMode.PreviousFolders;

            var folders = reopeningPrevious ? Settings.LastFolders : new[] { Settings.DefaultFolder };

            var usable = folders
                .Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (usable.Count == 0) return;

            // Only "reopen previous folders" carries these over. Opening the default folder
            // starts from the plain default (subfolders on, every type shown) even if the
            // last session had them set differently for a folder that may be unrelated.
            if (reopeningPrevious) IncludeSubfoldersBox.IsChecked = Settings.IncludeSubfolders;

            _currentFolders = usable;
            RefreshFolderPathText();
            RefreshButton.IsEnabled = true;

            await LoadCurrentFolderAsync();

            // The extension list only exists once RebuildTypeOptions has run inside the load
            // above, so the persisted unchecked set can only be applied after it completes.
            if (reopeningPrevious && Settings.UncheckedExtensions.Length > 0)
                RestoreExtensionFilter(Settings.UncheckedExtensions);
        }

        /// <summary>Unticks whichever extensions were unticked when the app last closed, then
        /// re-filters. Extensions not present in the current folder are ignored harmlessly.</summary>
        private void RestoreExtensionFilter(IEnumerable<string> uncheckedExtensions)
        {
            var toUncheck = new HashSet<string>(uncheckedExtensions, StringComparer.OrdinalIgnoreCase);
            if (toUncheck.Count == 0) return;

            bool wasSuppressed = _suppressEvents;
            _suppressEvents = true;

            foreach (var options in new[] { _imageExtensionOptions, _videoExtensionOptions, _audioExtensionOptions })
            {
                foreach (var option in options)
                {
                    if (toUncheck.Contains(option.Extension)) option.IsChecked = false;
                }
            }

            _suppressEvents = wasSuppressed;
            RefreshDisplayList();
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            // Fires on a libvlc background thread; touching the player synchronously from here
            // can deadlock, so hop back to the UI thread asynchronously.
            Dispatcher.BeginInvoke(() =>
            {
                if (_mediaPlayer == null) return;

                if (LoopCheckBox.IsChecked == true)
                {
                    // Stop() resets libvlc's internal state so the already-loaded Media can play
                    // again from the start.
                    _mediaPlayer.Stop();
                    _mediaPlayer.Play();
                    PlayPauseButton.Content = "Pause";
                }
                else
                {
                    _mediaPlayer.Stop();
                    PlayPauseButton.Content = "Play";
                    VideoPositionSlider.Value = 0;
                }
            });
        }

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            if (_mediaPlayer == null || _isDraggingSlider) return;
            if (PreviewVideo.Visibility != Visibility.Visible || _mediaPlayer.Media == null) return;

            var pos = _mediaPlayer.Position; // 0..1
            if (pos >= 0 && pos <= 1)
                VideoPositionSlider.Value = pos;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            SaveSettings();
            _thumbnailLoader.Stop();

            // Closing the finder runs its own OnClosed, which cancels any scan in flight.
            if (Settings.CloseDuplicateFinderOnExit) _duplicateFinder?.Close();

            // If DF is still open after the line above (CloseDuplicateFinderOnExit is off by
            // default), it needs to know this window is now unusable - a closed Window can
            // never be shown again, so "Open Folder in TagCat" has to stop offering to.
            _duplicateFinder?.NotifyHostWindowClosed();

            ClearFingerprintCacheIfRequested();
            ClearThumbnailCacheIfRequested();

            _positionTimer?.Stop();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }

        /// <summary>
        /// Honours "clear fingerprints on exit". Done after the finder has been asked to
        /// close, since it holds the database open while it is running. Failure is ignored:
        /// a cache file left behind is a minor cost, and nothing useful can be shown to
        /// someone who has already closed the window.
        /// </summary>
        private void ClearFingerprintCacheIfRequested()
        {
            if (!Settings.ClearFingerprintsOnExit) return;

            try
            {
                var path = Settings.FingerprintDatabasePath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Most likely still locked by a scan that has not finished unwinding.
            }
        }

        /// <summary>Honours "clear thumbnails on exit". The loader has just been stopped
        /// above, so nothing should still hold the file open.</summary>
        private void ClearThumbnailCacheIfRequested()
        {
            if (!Settings.ClearThumbnailsOnExit) return;

            try
            {
                var path = Settings.ThumbnailDatabasePath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // A leftover cache file is a minor cost, not worth surfacing on the way out.
            }
        }

        private void SaveSettings()
        {
            // Autoplay/Loop/StartMuted are deliberately NOT written here. They are startup
            // defaults now, edited only through Settings, and reading them back from the live
            // checkboxes would overwrite whatever default the user configured with whatever
            // they happened to toggle during this session - the opposite of "default".
            Settings.ThumbnailHeight = ThumbnailHeight;

            // Always recorded, even when the startup mode is DefaultFolder, so switching
            // the mode later has something to restore rather than starting empty. Same
            // reasoning for subfolders/type filter below - they ride along with LastFolders
            // and are only ever applied together, on the "reopen previous folders" path.
            Settings.LastFolders = _currentFolders.ToArray();
            Settings.IncludeSubfolders = IncludeSubfoldersBox.IsChecked == true;
            Settings.UncheckedExtensions = _imageExtensionOptions
                .Concat(_videoExtensionOptions)
                .Concat(_audioExtensionOptions)
                .Where(o => !o.IsChecked)
                .Select(o => o.Extension)
                .ToArray();

            Settings.Save();
        }

        private void RightPanelBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_initialized) return;
            UpdatePreviewHeight();
        }

        private void MainContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_initialized) return;
            ClampPreviewColumn();
        }

        /// <summary>
        /// The other half of the fix: MainContentGrid_SizeChanged only fires when the window
        /// itself is resized, because dragging a GridSplitter redistributes column widths
        /// without changing the grid's own size - so it never caught the actual drag that
        /// causes the overflow. DragCompleted, not DragDelta: ShowsPreview defers the real
        /// resize until the mouse is released, so DragDelta fires before there is anything
        /// to clamp yet.
        /// </summary>
        private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
            ClampPreviewColumn();

        /// <summary>
        /// Caps the preview column's pixel width to what is actually left over after the
        /// filter pane, both splitters and the thumbnail pane's minimum.
        ///
        /// The star-sized thumbnail column honours its MinWidth even when doing so makes the
        /// columns total wider than the grid - WPF simply lets the overflow run off the right
        /// edge rather than refusing. So dragging the splitter left, or shrinking the window
        /// afterwards, pushed the preview pane off-screen where it appeared to be cut in half.
        /// Clamping the one column that is measured in pixels keeps the total honest.
        /// </summary>
        private void ClampPreviewColumn()
        {
            double available = MainContentGrid.ActualWidth;
            if (available <= 0) return;

            // .Width.Value rather than .ActualWidth for these two pixel-sized columns: a
            // GridSplitter's resize (including the one that just triggered this call, via
            // DragCompleted) sets the Width property synchronously, but the Grid's next
            // layout pass - which is what actually updates ActualWidth - runs later. Reading
            // ActualWidth here could still see the pre-drag value and let an oversized column
            // through uncorrected, which was the reason the first attempt at this didn't work.
            double filterWidth = FilterColumn.Width.Value;
            double currentPreviewWidth = PreviewColumn.Width.Value;

            double maxPreview = available - filterWidth - 10 - MinThumbnailPaneWidth;

            // Never below the column's own declared minimum: on a very narrow window
            // something has to give, and the preview keeping its minimum is the lesser
            // evil compared to it collapsing entirely.
            maxPreview = Math.Max(maxPreview, PreviewColumn.MinWidth);

            if (currentPreviewWidth > maxPreview)
                PreviewColumn.Width = new GridLength(maxPreview);
        }

        /// <summary>Largest the preview area may grow vertically. Derived from the window
        /// rather than a fixed number, so expanding to native on a tall screen is not
        /// needlessly capped, while a small window still cannot push the tag controls below
        /// it out of reach.</summary>
        private double MaxPreviewHeight => Math.Max(200, ActualHeight * 0.6);

        /// <summary>
        /// Computes PreviewGrid's height from the pane's current width, except when
        /// overridden by Settings' "limit preview size to native resolution", which only ever
        /// pulls the automatic height down, never up, so a small image is never stretched
        /// past its real size and left looking soft.
        ///
        /// The expand-to-native case is handled separately in ExpandPreviewToNative, because
        /// it also has to widen the pane itself, not just this grid.
        /// </summary>
        internal void UpdatePreviewHeight()
        {
            double width = RightPanelBorder.ActualWidth;
            if (width <= 0) return;

            double height;
            if (_previewExpandedToNative && _currentNativeHeight > 0)
            {
                height = _currentNativeHeight;
            }
            else
            {
                height = width * 0.65;
                if (Settings.LimitPreviewToNativeResolution && _currentNativeHeight > 0)
                    height = Math.Min(height, _currentNativeHeight);
            }

            PreviewGrid.Height = Math.Clamp(height, 120, MaxPreviewHeight);
            UpdateVideoSurfaceSize();
        }

        /// <summary>
        /// Gives the VLC surface a box matching the video's aspect ratio exactly, centred in
        /// the preview area. VLC letterboxes internally when handed a mismatched box, and that
        /// hosted surface does not repaint in lockstep with WPF's layout, so those bars smeared
        /// and tore while resizing. With a correctly-proportioned box there are no bars.
        /// </summary>
        private void UpdateVideoSurfaceSize()
        {
            if (PreviewVideo.Visibility != Visibility.Visible) return;

            double boxWidth = PreviewGrid.ActualWidth;
            double boxHeight = PreviewGrid.Height;

            if (boxWidth <= 0 || boxHeight <= 0 || _currentNativeWidth <= 0 || _currentNativeHeight <= 0)
            {
                // Aspect unknown (an unparsable file): fall back to filling, which is the
                // old behaviour rather than a zero-sized invisible surface.
                PreviewVideo.Width = double.NaN;
                PreviewVideo.Height = double.NaN;
                return;
            }

            double aspect = (double)_currentNativeWidth / _currentNativeHeight;
            double fitted = boxWidth / aspect;

            if (fitted <= boxHeight)
            {
                PreviewVideo.Width = boxWidth;
                PreviewVideo.Height = fitted;
            }
            else
            {
                PreviewVideo.Width = boxHeight * aspect;
                PreviewVideo.Height = boxHeight;
            }
        }

        private void PreviewGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePreviewScaleText();
            UpdateVideoSurfaceSize();
        }

        /// <summary>Toggles expand-to-native, clicked from the "(native ###x###)" text.
        /// Sticky across selections - it stays on until clicked again, rather than resetting
        /// every time a different file is chosen.</summary>
        private void PreviewNativeSizeText_Click(object sender, MouseButtonEventArgs e)
        {
            if (_currentNativeWidth <= 0 || _currentNativeHeight <= 0) return;

            _previewExpandedToNative = !_previewExpandedToNative;

            if (_previewExpandedToNative) ExpandPreviewToNative();
            else UpdatePreviewHeight();
        }

        /// <summary>
        /// Grows the preview pane itself toward the media's native pixel size, not just the
        /// grid inside it - widening the column as well as raising the height. Both are capped
        /// by the same limits that apply to dragging the splitter by hand, so "as big as it
        /// needs" never means bigger than the window can hold.
        /// </summary>
        private void ExpandPreviewToNative()
        {
            // Padding either side of the content inside RightPanelBorder.
            const double panePadding = 24;

            double available = MainContentGrid.ActualWidth;
            if (available > 0)
            {
                double maxPreview = Math.Max(
                    PreviewColumn.MinWidth,
                    available - FilterColumn.ActualWidth - 10 - MinThumbnailPaneWidth);

                double wanted = Math.Min(_currentNativeWidth + panePadding, maxPreview);
                if (wanted > PreviewColumn.ActualWidth)
                    PreviewColumn.Width = new GridLength(wanted);
            }

            // Layout has to settle before ActualWidth reflects the new column, and the height
            // depends on it, so the height pass is deferred a beat rather than reading a stale
            // width and landing on the wrong number.
            Dispatcher.BeginInvoke(new Action(UpdatePreviewHeight),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Shows the preview's zoom level as a percentage of the media's actual native pixel
        /// resolution (100% = shown at true size), the way Photoshop/Explorer report zoom - not relative
        /// to the pane's own width. The native-size portion is a separate TextBlock so only it, not the
        /// percentage, is clickable.</summary>
        private void UpdatePreviewScaleText()
        {
            if (_currentNativeWidth <= 0 || _currentNativeHeight <= 0)
            {
                PreviewScalePercentText.Text = "Preview scale: --";
                PreviewNativeSizeText.Visibility = Visibility.Collapsed;
                return;
            }

            double containerWidth = PreviewGrid.ActualWidth;
            double containerHeight = PreviewGrid.ActualHeight;
            if (containerWidth <= 0 || containerHeight <= 0)
            {
                PreviewScalePercentText.Text = "Preview scale: --";
                PreviewNativeSizeText.Visibility = Visibility.Collapsed;
                return;
            }

            // Stretch="Uniform" fits the media inside PreviewGrid without distorting its aspect ratio,
            // so the actually-rendered content box can be narrower or shorter than the grid itself -
            // figure out which dimension is the limiting one to get the real rendered width.
            double mediaAspect = (double)_currentNativeWidth / _currentNativeHeight;
            double containerAspect = containerWidth / containerHeight;
            double renderedWidth = mediaAspect > containerAspect ? containerWidth : containerHeight * mediaAspect;

            int percent = (int)Math.Round(renderedWidth / _currentNativeWidth * 100);
            PreviewScalePercentText.Text = $"Preview scale: {percent}%";

            PreviewNativeSizeText.Text = $"(native {_currentNativeWidth}\u00d7{_currentNativeHeight})";
            PreviewNativeSizeText.Visibility = Visibility.Visible;
        }

        private static (int width, int height) GetNativeImageSize(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                // DelayCreation + CacheOption.None reads just the header/metadata, not the full pixel
                // data, so this stays fast even for large photos.
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
                return frame != null ? (frame.PixelWidth, frame.PixelHeight) : (0, 0);
            }
            catch
            {
                return (0, 0);
            }
        }

        // ---------- Folder loading ----------

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // .NET 9 added real multi-select support to FolderBrowserDialog (Multiselect +
            // SelectedPaths), so this is the same native folder-picker screen as before - just
            // check multiple folders in it directly, no follow-up prompt needed.
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Multiselect = true };
            if (_currentFolders.Count > 0) dialog.SelectedPath = _currentFolders[0];

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var picked = (dialog.SelectedPaths.Length > 0 ? dialog.SelectedPaths : new[] { dialog.SelectedPath }).ToList();

            // SetCurrentFolders does the rest: it already clears the per-folder state (type
            // filter, exclusions, folder browsing) that was set up for the previous selection
            // and would be meaningless against an unrelated new one.
            SelectFolderPopup.IsOpen = false;
            SetCurrentFolders(picked);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadCurrentFolder();

        // ---------- Select Folder dropdown / bookmarks ----------

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e) =>
            SelectFolderPopup.IsOpen = !SelectFolderPopup.IsOpen;

        /// <summary>Loads whatever path was typed into the folder box. Enter commits, Escape
        /// puts back what was showing before, so a half-typed path can be abandoned.</summary>
        private void FolderPathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RefreshFolderPathText();
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter) return;
            e.Handled = true;

            var typed = FolderPathBox.Text?.Trim().Trim('"') ?? "";
            if (string.IsNullOrEmpty(typed)) return;

            if (!Directory.Exists(typed))
            {
                MessageBox.Show(this, $"That folder doesn't exist:\n\n{typed}",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetCurrentFolders(new List<string> { Path.GetFullPath(typed) });
        }

        /// <summary>Switches to a new folder selection, clearing the per-folder state that was
        /// set up for the previous one - same reasoning as picking folders via Browse.</summary>
        private void SetCurrentFolders(List<string> folders) => _ = SetCurrentFoldersAsync(folders);

        /// <summary>
        /// Same as SetCurrentFolders, but awaitable - needed by OpenFolderAndHighlightAsync,
        /// which can only select the target file once loading has actually finished. Existing
        /// fire-and-forget callers are unaffected; SetCurrentFolders still behaves exactly as
        /// it did before.
        /// </summary>
        private async Task SetCurrentFoldersAsync(List<string> folders)
        {
            _currentFolders = folders;

            _imageExtensionOptions.Clear();
            _videoExtensionOptions.Clear();
            _audioExtensionOptions.Clear();
            _excludedSubfolderPaths.Clear();
            _browsingFolder = null;

            _suppressEvents = true;
            ShowFoldersInPaneBox.IsChecked = false;
            _suppressEvents = false;

            FolderBrowsingStrip.Visibility = Visibility.Collapsed;
            UpdateFolderOptionsEnabledState();

            RefreshFolderPathText();
            RefreshButton.IsEnabled = true;
            await LoadCurrentFolderAsync();
        }

        /// <summary>Puts the folder box back in step with what is actually loaded, after an
        /// abandoned edit or a change made elsewhere.</summary>
        private void RefreshFolderPathText()
        {
            if (_browsingFolder != null)
            {
                FolderPathBox.Text = _browsingFolder;
                return;
            }

            FolderPathBox.Text = _currentFolders.Count switch
            {
                0 => "",
                1 => _currentFolders[0],
                _ => $"{_currentFolders.Count} folders selected: {string.Join("; ", _currentFolders)}"
            };
        }

        /// <summary>
        /// Plain click loads that one bookmark. Ctrl+click adds or removes it from a running
        /// selection and loads everything selected together, so several bookmarked folders can
        /// be opened as one library without going through the folder picker.
        /// </summary>
        private void BookmarkItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not BookmarkRow row) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (ctrl)
            {
                row.IsSelected = !row.IsSelected;

                var selected = _bookmarkedFolders.Where(b => b.IsSelected).Select(b => b.Path).ToList();
                if (selected.Count == 0) return; // last one just un-picked; leave the view alone

                // The popup stays open so more can be added to the selection.
                LoadBookmarkFolders(selected);
                return;
            }

            foreach (var b in _bookmarkedFolders) b.IsSelected = false;
            SelectFolderPopup.IsOpen = false;
            LoadBookmarkFolders(new List<string> { row.Path });
        }

        /// <summary>Loads the given bookmarks, reporting any whose folder has since gone.</summary>
        private void LoadBookmarkFolders(List<string> paths)
        {
            var missing = paths.Where(p => !Directory.Exists(p)).ToList();
            var usable = paths.Where(Directory.Exists).ToList();

            if (usable.Count == 0)
            {
                MessageBox.Show(this,
                    $"That bookmarked folder no longer exists:\n\n{string.Join("\n", missing)}",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (missing.Count > 0)
            {
                MessageBox.Show(this,
                    $"These bookmarked folders no longer exist and were skipped:\n\n{string.Join("\n", missing)}",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            SetCurrentFolders(usable);
        }

        private void AddBookmark_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose a folder to bookmark."
            };

            // Offer wherever the person is currently looking as the obvious candidate.
            var startFrom = _browsingFolder is not null && Directory.Exists(_browsingFolder)
                ? _browsingFolder
                : _currentFolders.FirstOrDefault(Directory.Exists);
            if (startFrom != null) dialog.SelectedPath = startFrom;

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var chosen = Path.GetFullPath(dialog.SelectedPath);
            if (_bookmarkedFolders.Any(b => b.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "That folder is already bookmarked.",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _bookmarkedFolders.Add(new BookmarkRow(chosen));
            SaveBookmarks();
        }

        private void RemoveBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not BookmarkRow row) return;

            // Removing from within a multi-selection takes the whole selection, which is what
            // having picked several implies - but only when the clicked row is part of it.
            var selected = _bookmarkedFolders.Where(b => b.IsSelected).ToList();
            var toRemove = row.IsSelected && selected.Count > 1 ? selected : new List<BookmarkRow> { row };

            var message = toRemove.Count == 1
                ? $"Remove this bookmark?\n\n{toRemove[0].Path}\n\nThe folder itself is not touched."
                : $"Remove these {toRemove.Count} bookmarks?\n\n{string.Join("\n", toRemove.Select(b => b.Path))}\n\nThe folders themselves are not touched.";

            var confirm = MessageBox.Show(this, message,
                "Remove Bookmark", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            foreach (var b in toRemove) _bookmarkedFolders.Remove(b);
            SaveBookmarks();
        }

        private void SaveBookmarks()
        {
            Settings.BookmarkedFolders = _bookmarkedFolders.Select(b => b.Path).ToArray();
            Settings.Save();
            UpdateBookmarkEmptyState();
        }

        private void UpdateBookmarkEmptyState() =>
            NoBookmarksText.Visibility = _bookmarkedFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void FolderOptionsButton_Click(object sender, RoutedEventArgs e) =>
            FolderOptionsPopup.IsOpen = !FolderOptionsPopup.IsOpen;

        /// <summary>Just shows or hides the exclusion sub-list; the actual reload waits for
        /// Apply, same as every other control in this popup - several of them are usually
        /// changed together, and reloading on each individual click would rescan the folder
        /// once per click instead of once for the whole batch.</summary>
        private void IncludeSubfolders_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppressEvents) return;

            // The two are independent again: browsing a folder tree while also pulling in
            // everything beneath the folder you land on is a reasonable thing to want.
            UpdateFolderOptionsEnabledState();
            if (_currentFolders.Count > 0) LoadCurrentFolder();
        }

        /// <summary>
        /// Greys out whichever options don't currently apply rather than hiding them, so the
        /// panel keeps its shape and it stays visible that the options exist. Excluding
        /// subfolders only means anything when subfolders are being included at all, and
        /// neither applies while browsing one folder at a time.
        /// </summary>
        /// <summary>
        /// The two folder modes are mutually exclusive, so ticking either unticks the other.
        /// Previously only browsing cleared subfolders, and never the reverse, so Show Folders
        /// stayed clickable while Include Subfolders was on and the two could both look active.
        /// Neither is greyed out now - either can be clicked directly to switch modes, or
        /// clicked again to turn off and go back to a plain single-folder view.
        /// </summary>
        private void UpdateFolderOptionsEnabledState()
        {
            // Only the exclusion list is ever disabled: excluding subfolders is meaningless
            // unless subfolders are actually being included.
            ExcludeSubfoldersPanel.IsEnabled = IncludeSubfoldersBox.IsChecked == true;
        }

        private static bool IsArrowKey(Key key) =>
            key is Key.Left or Key.Right or Key.Up or Key.Down;

        /// <summary>
        /// Moves the selection one tile at a time. Left/Right step through the list; Up/Down
        /// jump by however many tiles fit across the grid right now, so they move visually up
        /// and down a row rather than by some fixed guess. Shift extends the selection from
        /// the anchor, matching shift-click.
        /// </summary>
        private bool MoveSelectionByArrow(Key key)
        {
            if (_displayedItems.Count == 0) return false;

            int columns = Math.Max(1, (int)(MediaItemsControl.ActualWidth / Math.Max(1, TileCellWidth)));

            int current = _selectedItem != null ? _displayedItems.IndexOf(_selectedItem) : -1;
            if (current < 0)
            {
                // Nothing selected yet: first arrow press lands on the first tile rather than
                // moving relative to a selection that doesn't exist.
                SelectSingle(0);
                return true;
            }

            int next = key switch
            {
                Key.Left => current - 1,
                Key.Right => current + 1,
                Key.Up => current - columns,
                Key.Down => current + columns,
                _ => current
            };

            // Up/Down off the top or bottom row would otherwise jump to the far end of the
            // list; staying put is less jarring. Left/Right simply stop at the ends.
            if (next < 0 || next >= _displayedItems.Count) return true;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                ExtendSelectionTo(next);
            else
                SelectSingle(next);

            return true;
        }

        private void SelectSingle(int index)
        {
            foreach (var item in _displayedItems) item.IsSelected = false;

            var target = _displayedItems[index];
            target.IsSelected = true;
            _selectionAnchor = target;

            ShowItemDetails(target);
            UpdateRemoveTagOptions();
            UpdateSelectedCount();
            ScrollItemIntoView(index);
        }

        private void ExtendSelectionTo(int index)
        {
            int anchor = _selectionAnchor != null ? _displayedItems.IndexOf(_selectionAnchor) : index;
            if (anchor < 0) anchor = index;

            int lo = Math.Min(anchor, index), hi = Math.Max(anchor, index);

            for (int i = 0; i < _displayedItems.Count; i++)
                _displayedItems[i].IsSelected = i >= lo && i <= hi;

            ShowItemDetails(_displayedItems[index]);
            UpdateRemoveTagOptions();
            UpdateSelectedCount();
            ScrollItemIntoView(index);
        }

        /// <summary>Keeps the newly selected tile on screen. The virtualizing panel implements
        /// MakeVisible, so this works whether or not the tile is currently realized.</summary>
        private void ScrollItemIntoView(int index)
        {
            if (MediaItemsControl.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                container.BringIntoView();
                return;
            }

            // Not realized yet: nudge the panel directly so it scrolls to that row.
            var panel = FindVisualChild<Controls.VirtualizingWrapPanel>(MediaItemsControl);
            if (panel == null) return;

            int columns = Math.Max(1, (int)(MediaItemsControl.ActualWidth / Math.Max(1, TileCellWidth)));
            panel.SetVerticalOffset(index / columns * TileHeight);
        }

        /// <summary>Reloads whatever is currently open, for settings that change what belongs
        /// in the grid (hidden files). Safe to call when nothing is loaded.</summary>
        internal void ReloadCurrentFolders()
        {
            if (_currentFolders.Count > 0) LoadCurrentFolder();
        }

        private void AddSubfolderExclusion_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose a subfolder to exclude from the recursive scan."
            };

            // Starts wherever the person is actually looking - the folder being browsed if
            // that's active, otherwise the first loaded folder - rather than always jumping
            // back to the top-level selection.
            var startFrom = _browsingFolder is not null && Directory.Exists(_browsingFolder)
                ? _browsingFolder
                : _currentFolders.FirstOrDefault(Directory.Exists);
            if (startFrom != null) dialog.SelectedPath = startFrom;

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var chosen = Path.GetFullPath(dialog.SelectedPath);
            if (!_excludedSubfolderPaths.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                _excludedSubfolderPaths.Add(chosen);

            NoExclusionsText.Visibility = _excludedSubfolderPaths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_currentFolders.Count > 0) LoadCurrentFolder();
        }

        private void RemoveSubfolderExclusion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string path)
                _excludedSubfolderPaths.Remove(path);

            NoExclusionsText.Visibility = _excludedSubfolderPaths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_currentFolders.Count > 0) LoadCurrentFolder();
        }

        // ---------- Folder browsing (Show Folders in Thumbnail Pane) ----------

        private void ShowFoldersInPane_Changed(object sender, RoutedEventArgs e)
        {
            // _suppressEvents matters more than usual now this applies immediately: code that
            // resets this checkbox would otherwise trigger a reload, and the reset inside
            // LoadCurrentFolderAsync would re-enter the very method doing the resetting.
            if (!_initialized || _suppressEvents) return;

            bool wantsBrowsing = ShowFoldersInPaneBox.IsChecked == true;

            if (wantsBrowsing && _browsingFolder == null)
            {
                // Entering fresh: start at the first originally selected folder. With several
                // selected, browsing follows just this one - there is no single parent that
                // would make "up" mean anything across multiple unrelated folder trees.
                _browsingFolder = _currentFolders.FirstOrDefault(Directory.Exists);
            }
            else if (!wantsBrowsing && _browsingFolder != null)
            {
                // Switching browsing off keeps wherever you navigated to, rather than snapping
                // back to the folder originally picked. Having clicked down into a folder, that
                // is what you are looking at, so it becomes the selection.
                _currentFolders = new List<string> { _browsingFolder };
                FolderPathBox.Text = _browsingFolder;
                _browsingFolder = null;
            }

            UpdateFolderOptionsEnabledState();
            if (_currentFolders.Count > 0) LoadCurrentFolder();
        }

        private void UpDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (_browsingFolder == null) return;

            var parent = Directory.GetParent(_browsingFolder);
            if (parent == null) return; // already at a drive root

            _browsingFolder = parent.FullName;
            LoadCurrentFolder();
        }

        private void FolderTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not FolderTile tile) return;

            _browsingFolder = tile.FullPath;
            LoadCurrentFolder();
        }

        /// <summary>Immediate subfolders of _browsingFolder, as navigable tiles. A folder
        /// that can't be read (permissions) is skipped rather than failing the whole strip.</summary>
        private void RebuildFolderTiles()
        {
            _folderTiles.Clear();

            if (_browsingFolder != null && Directory.Exists(_browsingFolder))
            {
                try
                {
                    var subfolders = Directory.EnumerateDirectories(_browsingFolder)
                        .Where(d => Settings.ShowHiddenFiles ||
                                    !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden))
                        .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

                    foreach (var dir in subfolders) _folderTiles.Add(new FolderTile(dir));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            NoSubfoldersText.Visibility = _folderTiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LoadCurrentFolder() => await LoadCurrentFolderAsync();

        private async Task LoadCurrentFolderAsync()
        {
            if (_browsingFolder != null)
            {
                if (!Directory.Exists(_browsingFolder))
                {
                    // Renamed or deleted from outside the app since it was entered - fall back
                    // to the normal view instead of staying pointed at a folder that's gone.
                    _browsingFolder = null;

                    _suppressEvents = true;
                    ShowFoldersInPaneBox.IsChecked = false;
                    _suppressEvents = false;

                    FolderBrowsingStrip.Visibility = Visibility.Collapsed;
                    UpdateFolderOptionsEnabledState();
                }
                else
                {
                    StatusText.Text = "Loading...";
                    var browsing = _browsingFolder;

                    // Both options can be on together now, so browsing respects Include
                    // Sub-folder Contents (and its exclusions) for the folder landed on.
                    bool browseSub = IncludeSubfoldersBox.IsChecked == true;
                    var browseExclusions = browseSub ? _excludedSubfolderPaths.ToList() : new List<string>();
                    bool browseHidden = Settings.ShowHiddenFiles;

                    _allItems = await Task.Run(() =>
                        LibraryService.LoadFolders(new[] { browsing }, browseSub, browseExclusions, browseHidden));
                    FinishLoad();

                    FolderPathBox.Text = _browsingFolder;
                    var name = Path.GetFileName(_browsingFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    BrowsingFolderText.Text = string.IsNullOrEmpty(name) ? _browsingFolder : name;
                    FolderBrowsingStrip.Visibility = Visibility.Visible;
                    UpDirectoryButton.IsEnabled = Directory.GetParent(_browsingFolder) != null;
                    RebuildFolderTiles();
                    return;
                }
            }

            var validFolders = _currentFolders.Where(Directory.Exists).ToList();
            if (validFolders.Count == 0) return;

            StatusText.Text = "Loading...";
            bool includeSub = IncludeSubfoldersBox.IsChecked == true;
            var exclusions = includeSub ? _excludedSubfolderPaths.ToList() : new List<string>();

            bool showHidden = Settings.ShowHiddenFiles;
            _allItems = await Task.Run(() => LibraryService.LoadFolders(validFolders, includeSub, exclusions, showHidden));
            FinishLoad();

            RefreshFolderPathText();
            FolderBrowsingStrip.Visibility = Visibility.Collapsed;
        }

        /// <summary>The tail both loading paths share: refresh every dependent list and kick
        /// off background thumbnail generation.</summary>
        private void FinishLoad()
        {
            RebuildTagOptions();
            RebuildTypeOptions();
            RefreshDisplayList();
            ClearSelection();

            StatusText.Text = $"Loaded {_allItems.Count} files.";

            // Thumbnails are generated in the background, visible rows first. RefreshDisplayList
            // has already run, so the loader's working set is the filtered, sorted list.
            _thumbnailLoader.Reset();
            _thumbnailLoader.SetWorkingSet(_displayedItems, resetHandled: true);
        }

        // ---------- Tag filter list ----------

        private void RebuildTagOptions()
        {
            var counts = _allItems
                .SelectMany(i => i.Tags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            // Which tags were being filtered on, so the rebuild doesn't wipe the user's
            // filter. Tagging calls this to refresh counts, and clearing the checkboxes
            // meant every tag edit silently reset the view to "everything".
            var previouslyChecked = _tagOptions
                .Where(o => o.IsChecked && !o.IsNoTagsOption)
                .Select(o => o.Tag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool noTagsWasChecked = _tagOptions.Any(o => o.IsChecked && o.IsNoTagsOption);

            // Suppressed because assigning IsChecked below raises the same change event the
            // checkboxes do, which would trigger a refresh per tag mid-rebuild.
            bool wasSuppressed = _suppressEvents;
            _suppressEvents = true;

            _tagOptions.Clear();
            foreach (var tag in counts.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                var opt = new TagOption(tag)
                {
                    Count = counts[tag],
                    IsAvailable = true,
                    IsChecked = previouslyChecked.Contains(tag)
                };
                _tagOptions.Add(opt);
            }

            int noTagsCount = _allItems.Count(i => i.Tags.Count == 0);
            _tagOptions.Add(new TagOption("No tags")
            {
                IsNoTagsOption = true,
                Count = noTagsCount,
                IsAvailable = true,
                IsChecked = noTagsWasChecked
            });

            _suppressEvents = wasSuppressed;

            ReorderTagOptions();
            RebuildExcludeTagOptions(counts, noTagsCount);
        }

        /// <summary>
        /// Mirrors the include list, and preserves its ticks across a rebuild for the same
        /// reason: tagging a file refreshes counts, and losing the exclusions there would
        /// silently widen the view every time a tag was edited.
        /// </summary>
        private void RebuildExcludeTagOptions(Dictionary<string, int> counts, int noTagsCount)
        {
            var previouslyChecked = _excludeTagOptions
                .Where(o => o.IsChecked && !o.IsNoTagsOption)
                .Select(o => o.Tag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool noTagsWasChecked = _excludeTagOptions.Any(o => o.IsChecked && o.IsNoTagsOption);

            bool wasSuppressed = _suppressEvents;
            _suppressEvents = true;

            _excludeTagOptions.Clear();
            foreach (var tag in counts.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                _excludeTagOptions.Add(new ExcludeTagOption(tag)
                {
                    Count = counts[tag],
                    IsChecked = previouslyChecked.Contains(tag)
                });
            }

            // "Exclude: No tags" is the way to say "only files I've actually tagged".
            _excludeTagOptions.Add(new ExcludeTagOption("No tags")
            {
                IsNoTagsOption = true,
                Count = noTagsCount,
                IsChecked = noTagsWasChecked
            });

            _suppressEvents = wasSuppressed;

            // Same trigger as the exclude list rebuilding, since both need to stay in step
            // with whatever tags currently exist.
            BooleanTagChipList.ItemsSource = _tagOptions
                .Where(o => !o.IsNoTagsOption)
                .Select(o => o.Tag)
                .ToList();

            NoExcludableTagsText.Visibility = counts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAdvancedFilterSummary();
        }

        /// <summary>
        /// Summarises anything non-default onto the collapsed header. Without this, an
        /// exclusion or an Any match left set would silently hide files with no visible reason
        /// once the section is folded away.
        /// </summary>
        private void UpdateAdvancedFilterSummary()
        {
            var parts = new List<string>();

            if (MatchAnyTagsRadio.IsChecked == true) parts.Add("match any");

            int excluded = _excludeTagOptions.Count(o => o.IsChecked);
            if (excluded > 0) parts.Add($"{excluded} excluded");

            if (_booleanTagExpression != null) parts.Add("boolean filter");

            AdvancedFilterSummaryText.Text = parts.Count == 0 ? "" : $"({string.Join(", ", parts)})";
            ExcludeTagsHeaderCountText.Text = excluded == 0 ? "" : $"({excluded})";
        }

        /// <summary>
        /// Rebuilds one category's extension list, keeping any box the user had unticked.
        /// An extension appearing for the first time defaults to ticked, which matches what
        /// happens when a folder is first loaded.
        /// </summary>
        private static void RebuildExtensionOptions(
            ObservableCollection<ExtensionFilterOption> options,
            Dictionary<string, int> counts)
        {
            var previouslyUnchecked = options
                .Where(o => !o.IsChecked)
                .Select(o => o.Extension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            options.Clear();
            foreach (var ext in counts.Keys.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                options.Add(new ExtensionFilterOption(ext)
                {
                    Count = counts[ext],
                    IsChecked = !previouslyUnchecked.Contains(ext)
                });
            }
        }

        private void RebuildTypeOptions()
        {
            var imgCounts = _allItems.Where(i => i.Kind == MediaKind.Image)
                .GroupBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var vidCounts = _allItems.Where(i => i.Kind == MediaKind.Video)
                .GroupBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var audioCounts = _allItems.Where(i => i.Kind == MediaKind.Audio)
                .GroupBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            bool wasSuppressed = _suppressEvents;
            _suppressEvents = true;

            RebuildExtensionOptions(_imageExtensionOptions, imgCounts);
            RebuildExtensionOptions(_videoExtensionOptions, vidCounts);
            RebuildExtensionOptions(_audioExtensionOptions, audioCounts);

            _suppressEvents = wasSuppressed;
        }

        /// <summary>
        /// Refreshes the counts beside each tag and decides which ones to grey out.
        ///
        /// The counts come from the results as they stand, so they read as "ticking this as well
        /// narrows things to N" - but what greys a tag out is now only whether it has been
        /// excluded. Greying by count was wrong in Any mode, where a tag that matches nothing
        /// right now would still *add* files if ticked, so showing it as unavailable was the
        /// opposite of the truth.
        /// </summary>
        private void UpdateTagAvailability(IEnumerable<MediaItem> currentlyFiltered, IEnumerable<MediaItem> beforeTagFilter)
        {
            var filteredList = currentlyFiltered.ToList();
            var candidates = beforeTagFilter.ToList();
            bool matchAny = MatchAnyTagsRadio.IsChecked == true;

            var excluded = _excludeTagOptions
                .Where(o => o.IsChecked && !o.IsNoTagsOption)
                .Select(o => o.Tag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool untaggedExcluded = _excludeTagOptions.Any(o => o.IsChecked && o.IsNoTagsOption);

            foreach (var opt in _tagOptions)
            {
                if (!opt.IsChecked)
                {
                    // In Any mode a tag widens the results, so count against everything the
                    // other filters left rather than against the current tag match.
                    var basis = matchAny ? candidates : filteredList;

                    opt.Count = opt.IsNoTagsOption
                        ? basis.Count(i => i.Tags.Count == 0)
                        : basis.Count(i => i.Tags.Contains(opt.Tag, StringComparer.OrdinalIgnoreCase));
                }

                // Excluding a tag and also including it cancel out, so the include entry is
                // greyed to show it has no effect while the exclusion stands.
                opt.IsAvailable = opt.IsNoTagsOption ? !untaggedExcluded : !excluded.Contains(opt.Tag);
            }

            UpdateExcludeTagAvailability();
            ReorderTagOptions();
        }

        /// <summary>Greys an exclusion entry whose tag is also ticked as an include, mirroring
        /// the other direction - the two contradict, so neither is left looking active.</summary>
        private void UpdateExcludeTagAvailability()
        {
            var included = _tagOptions
                .Where(o => o.IsChecked && !o.IsNoTagsOption)
                .Select(o => o.Tag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool untaggedIncluded = _tagOptions.Any(o => o.IsChecked && o.IsNoTagsOption);

            foreach (var opt in _excludeTagOptions)
                opt.IsAvailable = opt.IsNoTagsOption ? !untaggedIncluded : !included.Contains(opt.Tag);
        }

        /// <summary>Moves checked tags, then available tags, to the top of the checklist; unavailable
        /// ones sink down. The synthetic "No tags" entry always stays pinned to the very bottom.</summary>
        private void ReorderTagOptions()
        {
            var sorted = _tagOptions
                .OrderBy(o => o.IsNoTagsOption) // false sorts before true -> pins "No tags" last
                .ThenByDescending(o => o.IsChecked)
                .ThenByDescending(o => o.IsAvailable)
                .ThenBy(o => o.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                int oldIndex = _tagOptions.IndexOf(sorted[i]);
                if (oldIndex != i) _tagOptions.Move(oldIndex, i);
            }
        }

        private void TagCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || !_initialized) return;
            RefreshDisplayList();
        }

        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            _suppressEvents = true;
            foreach (var opt in _tagOptions) opt.IsChecked = false;
            foreach (var opt in _excludeTagOptions) opt.IsChecked = false;
            MatchAllTagsRadio.IsChecked = true;
            BooleanExpressionBox.Clear();
            _suppressEvents = false;

            _booleanTagExpression = null;
            BooleanExpressionErrorText.Visibility = Visibility.Collapsed;

            UpdateAdvancedFilterSummary();
            RefreshDisplayList();
        }

        private void TypeFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppressEvents) return;
            RefreshDisplayList();
        }

        private void ImageCategory_Click(object sender, RoutedEventArgs e) => SetCategoryAll(_imageExtensionOptions, ShowImagesBox.IsChecked == true);
        private void VideoCategory_Click(object sender, RoutedEventArgs e) => SetCategoryAll(_videoExtensionOptions, ShowVideosBox.IsChecked == true);
        private void AudioCategory_Click(object sender, RoutedEventArgs e) => SetCategoryAll(_audioExtensionOptions, ShowAudioBox.IsChecked == true);

        /// <summary>Ticking/unticking a category checkbox ticks/unticks every extension checkbox
        /// underneath it. Click (not Checked/Unchecked) is used to wire this up, since Click only
        /// fires for real user interaction - it won't re-fire while UpdateTypeFilterUi below is
        /// syncing the checkbox's own display state from its children, which avoids feedback loops.</summary>
        private void SetCategoryAll(ObservableCollection<ExtensionFilterOption> options, bool check)
        {
            _suppressEvents = true; // batch the per-extension IsChecked writes into a single refresh
            foreach (var opt in options) opt.IsChecked = check;
            _suppressEvents = false;

            RefreshDisplayList();
        }

        /// <summary>Sets a category checkbox to checked/unchecked/indeterminate (greyed) to reflect
        /// whether all, none, or some of its extensions are currently checked.</summary>
        private static void SyncCategoryCheckboxState(CheckBox box, IEnumerable<ExtensionFilterOption> options)
        {
            var list = options.ToList();
            if (list.Count == 0) { box.IsChecked = false; return; }
            int checkedCount = list.Count(o => o.IsChecked);
            box.IsChecked = checkedCount == 0 ? false : checkedCount == list.Count ? true : (bool?)null;
        }

        /// <summary>Refreshes the "(shown of total)" counters and the category checkboxes'
        /// checked/unchecked/indeterminate state. Called once per RefreshDisplayList so it always
        /// reflects the current filter results.</summary>
        private void UpdateTypeFilterUi()
        {
            SyncCategoryCheckboxState(ShowImagesBox, _imageExtensionOptions);
            SyncCategoryCheckboxState(ShowVideosBox, _videoExtensionOptions);
            SyncCategoryCheckboxState(ShowAudioBox, _audioExtensionOptions);

            // These counts reflect the type filter only (which extensions are checked, plus
            // the size range below) - they deliberately ignore the tag filter, so toggling
            // tags never makes the "Filter by type" counters move. The "Filter by tags"
            // counter below covers the combined effect.
            int totalImages = _imageExtensionOptions.Sum(o => o.Count);
            int totalVideos = _videoExtensionOptions.Sum(o => o.Count);
            int totalAudio = _audioExtensionOptions.Sum(o => o.Count);

            int shownImages, shownVideos, shownAudio;
            if (SizeFilterBox.IsChecked == true)
            {
                // The size range makes the extension counts alone insufficient, so this
                // walks the library directly rather than summing precomputed totals.
                shownImages = _allItems.Count(i => i.Kind == MediaKind.Image && IsTypeVisible(i) && IsSizeVisible(i));
                shownVideos = _allItems.Count(i => i.Kind == MediaKind.Video && IsTypeVisible(i) && IsSizeVisible(i));
                shownAudio = _allItems.Count(i => i.Kind == MediaKind.Audio && IsTypeVisible(i) && IsSizeVisible(i));
            }
            else
            {
                shownImages = _imageExtensionOptions.Where(o => o.IsChecked).Sum(o => o.Count);
                shownVideos = _videoExtensionOptions.Where(o => o.IsChecked).Sum(o => o.Count);
                shownAudio = _audioExtensionOptions.Where(o => o.IsChecked).Sum(o => o.Count);
            }

            ShowImagesBox.Content = $"Images ({shownImages} of {totalImages})";
            ShowVideosBox.Content = $"Videos ({shownVideos} of {totalVideos})";
            ShowAudioBox.Content = $"Audio ({shownAudio} of {totalAudio})";

            int typeFilteredCount = shownImages + shownVideos + shownAudio;
            TypeFilterTotalText.Text = $"({typeFilteredCount} of {_allItems.Count})";

            // "Filter by tags" counter: how many of the files that already passed the type filter
            // are still visible once the tag filter is applied too.
            TagFilterTotalText.Text = $"({_displayedItems.Count} of {typeFilteredCount})";
        }

        // ---------- Sorting + filtering ----------

        private void SortOption_Changed(object sender, SelectionChangedEventArgs e)
        {
            RefreshDisplayList();
        }

        private IEnumerable<MediaItem> ApplyTypeFilter(IEnumerable<MediaItem> items) =>
            items.Where(i => IsTypeVisible(i) && IsSizeVisible(i));

        private bool IsTypeVisible(MediaItem item)
        {
            if (item.Kind == MediaKind.Image)
            {
                var opt = _imageExtensionOptions.FirstOrDefault(o => o.Extension.Equals(item.Extension, StringComparison.OrdinalIgnoreCase));
                return opt == null || opt.IsChecked;
            }
            if (item.Kind == MediaKind.Video)
            {
                var opt = _videoExtensionOptions.FirstOrDefault(o => o.Extension.Equals(item.Extension, StringComparison.OrdinalIgnoreCase));
                return opt == null || opt.IsChecked;
            }
            if (item.Kind == MediaKind.Audio)
            {
                var opt = _audioExtensionOptions.FirstOrDefault(o => o.Extension.Equals(item.Extension, StringComparison.OrdinalIgnoreCase));
                return opt == null || opt.IsChecked;
            }
            return true;
        }

        /// <summary>Lives under "Filter by type" alongside the extension checklists, so it
        /// shares their on/off pattern: unticked (default), the size boxes are ignored
        /// entirely and every file passes regardless of what is typed in them.</summary>
        private bool IsSizeVisible(MediaItem item)
        {
            if (SizeFilterBox.IsChecked != true) return true;

            if (TryParseSizeBound(SizeMinBox.Text, SizeMinUnitCombo, out long minBytes) && item.SizeInBytes < minBytes)
                return false;
            if (TryParseSizeBound(SizeMaxBox.Text, SizeMaxUnitCombo, out long maxBytes) && item.SizeInBytes > maxBytes)
                return false;

            return true;
        }

        /// <summary>An empty or unparsable box means "no limit on that side" rather than an
        /// error - the person may only care about one bound, or may be mid-way through typing
        /// the other, and neither should make every file vanish from the grid.</summary>
        private static bool TryParseSizeBound(string? text, ComboBox unitCombo, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;
            if (value < 0) return false;

            double multiplier = unitCombo.SelectedIndex switch
            {
                0 => 1024d,                    // KB
                2 => 1024d * 1024 * 1024,       // GB
                _ => 1024d * 1024                // MB (also the fallback if nothing is selected)
            };

            bytes = (long)(value * multiplier);
            return true;
        }

        private void SizeFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            RefreshDisplayList();
        }

        private void SizeFilterText_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_initialized) return;
            RefreshDisplayList();
        }

        private void SizeFilterUnit_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            RefreshDisplayList();
        }

        /// <summary>
        /// Combines three independent choices: which tags to include, whether a file needs all
        /// of them or just one, and which tags disqualify a file outright.
        ///
        /// Exclusion is applied last and always wins, so "beach or mountains, but never blurry"
        /// works regardless of the match mode - which is the main thing this exists for.
        /// </summary>
        private IEnumerable<MediaItem> ApplyTagFilter(IEnumerable<MediaItem> items)
        {
            var selectedTags = _tagOptions.Where(o => o.IsChecked && !o.IsNoTagsOption).Select(o => o.Tag).ToList();
            bool noTagsSelected = _tagOptions.Any(o => o.IsChecked && o.IsNoTagsOption);
            bool matchAny = MatchAnyTagsRadio.IsChecked == true;

            var excludedTags = _excludeTagOptions.Where(o => o.IsChecked && !o.IsNoTagsOption).Select(o => o.Tag).ToList();
            bool excludeUntagged = _excludeTagOptions.Any(o => o.IsChecked && o.IsNoTagsOption);

            var result = items;

            if (selectedTags.Count > 0)
            {
                if (matchAny)
                {
                    // "No tags" alongside named tags reads as another alternative in Any mode -
                    // show untagged files as well as ones carrying any of the ticked tags.
                    result = result.Where(i =>
                        (noTagsSelected && i.Tags.Count == 0) ||
                        selectedTags.Any(t => i.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
                }
                else
                {
                    // In All mode "No tags" plus a named tag is a contradiction - a file cannot
                    // have no tags and also carry one - so the named tags are what counts.
                    result = result.Where(i => selectedTags.All(t => i.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
                }
            }
            else if (noTagsSelected)
            {
                result = result.Where(i => i.Tags.Count == 0);
            }

            if (excludedTags.Count > 0)
                result = result.Where(i => !excludedTags.Any(t => i.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));

            if (excludeUntagged)
                result = result.Where(i => i.Tags.Count > 0);

            // Additional, not a replacement: a file has to satisfy everything above as well
            // as this, so it composes with the checklist rather than overriding it.
            if (_booleanTagExpression != null)
                result = result.Where(i => _booleanTagExpression.Evaluate(i.Tags));

            return result;
        }

        /// <summary>Null means no boolean filter is active - either the box is empty, or
        /// what's typed doesn't parse yet. Recompiled on every keystroke rather than only on
        /// a separate Apply step; a bad partial expression while still typing just means the
        /// filter contributes nothing until it parses, rather than blocking the view.</summary>
        private BooleanTagExpression? _booleanTagExpression;

        private void BooleanTagButton_Click(object sender, RoutedEventArgs e) =>
            BooleanTagPopup.IsOpen = !BooleanTagPopup.IsOpen;

        /// <summary>
        /// Toggles an inline panel rather than a MessageBox - the Boolean Tags popup has
        /// StaysOpen="False", so opening a genuinely modal dialog on top of it would steal
        /// focus and close the popup out from under whatever was being typed.
        /// </summary>
        private void BooleanHelpLink_Click(object sender, RoutedEventArgs e) =>
            BooleanHelpPanel.Visibility = BooleanHelpPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

        private void BooleanExpressionBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized) return;

            var text = BooleanExpressionBox.Text?.Trim() ?? "";

            if (text.Length == 0)
            {
                _booleanTagExpression = null;
                BooleanExpressionErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                try
                {
                    var expression = BooleanTagExpression.Parse(text);

                    var knownTags = new HashSet<string>(
                        _tagOptions.Where(o => !o.IsNoTagsOption).Select(o => o.Tag),
                        StringComparer.OrdinalIgnoreCase);

                    var unknown = expression.ReferencedTags()
                        .Where(t => !knownTags.Contains(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (unknown.Count > 0)
                    {
                        _booleanTagExpression = null;
                        BooleanExpressionErrorText.Text = unknown.Count == 1
                            ? $"Unknown tag: {unknown[0]}"
                            : $"Unknown tags: {string.Join(", ", unknown)}";
                        BooleanExpressionErrorText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _booleanTagExpression = expression;
                        BooleanExpressionErrorText.Visibility = Visibility.Collapsed;
                    }
                }
                catch (FormatException ex)
                {
                    _booleanTagExpression = null;
                    BooleanExpressionErrorText.Text = ex.Message;
                    BooleanExpressionErrorText.Visibility = Visibility.Visible;
                }
            }

            UpdateAdvancedFilterSummary();
            RefreshDisplayList();
        }

        /// <summary>Inserts at the caret rather than appending, so a tag can be dropped into
        /// the middle of an expression already being edited.</summary>
        private void BooleanTagChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;

            int caret = BooleanExpressionBox.CaretIndex;
            string insert = tag + " ";

            BooleanExpressionBox.Text = BooleanExpressionBox.Text.Insert(caret, insert);
            BooleanExpressionBox.CaretIndex = caret + insert.Length;
            BooleanExpressionBox.Focus();
        }

        private void BooleanExpressionClear_Click(object sender, RoutedEventArgs e)
        {
            BooleanExpressionBox.Clear();
            BooleanExpressionBox.Focus();
        }

        private void BooleanTagPopupClose_Click(object sender, RoutedEventArgs e) =>
            BooleanTagPopup.IsOpen = false;

        private void TagMatchMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppressEvents) return;
            UpdateAdvancedFilterSummary();
            RefreshDisplayList();
        }

        private void ExcludeTagCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _suppressEvents) return;
            UpdateAdvancedFilterSummary();
            RefreshDisplayList();
        }

        private void ResetAdvancedFilters_Click(object sender, RoutedEventArgs e)
        {
            _suppressEvents = true;
            foreach (var opt in _excludeTagOptions) opt.IsChecked = false;
            MatchAllTagsRadio.IsChecked = true;
            BooleanExpressionBox.Clear();
            _suppressEvents = false;

            // Cleared directly rather than left to TextChanged, which is suppressed above -
            // Clear() on an already-empty box raises no event to react to anyway.
            _booleanTagExpression = null;
            BooleanExpressionErrorText.Visibility = Visibility.Collapsed;

            UpdateAdvancedFilterSummary();
            RefreshDisplayList();
        }

        /// <summary>
        /// Free-text filter over the whole filename, tag block included, so typing part of a
        /// tag works as well as part of a name. Multiple words must all appear but may appear
        /// in any order, which is how people expect a search box to behave.
        /// </summary>
        private IEnumerable<MediaItem> ApplySearchFilter(IEnumerable<MediaItem> items)
        {
            var query = SearchBox?.Text?.Trim();
            if (string.IsNullOrEmpty(query)) return items;

            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return items.Where(i => terms.All(t =>
                i.FileName.Contains(t, StringComparison.OrdinalIgnoreCase)));
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized) return;

            ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            RefreshDisplayList();
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void RefreshDisplayList()
        {
            if (!_initialized) return; // window is still being constructed by WPF; controls may not all exist yet

            var field = SortFieldCombo.SelectedIndex switch
            {
                1 => SortField.DateModified,
                2 => SortField.DateCreated,
                3 => SortField.Size,
                4 => SortField.Random,
                _ => SortField.FileName
            };
            bool ascending = SortDirCombo.SelectedIndex == 0;

            var typeFiltered = ApplyTypeFilter(_allItems);
            var searched = ApplySearchFilter(typeFiltered);
            var searchedList = searched.ToList();
            var filtered = ApplyTagFilter(searchedList).ToList();
            UpdateTagAvailability(filtered, searchedList);

            var sorted = LibraryService.Sort(filtered, field, ascending);

            _displayedItems.Clear();
            foreach (var item in sorted) _displayedItems.Add(item);

            // The running count lives above "Selected File" in the right panel now, which
            // leaves StatusText in the toolbar free for transient messages ("Loading...",
            // "Tags saved.") without one overwriting the other.
            FilesShownText.Text = $"{_displayedItems.Count} of {_allItems.Count} Total Files Shown.";
            UpdateSelectedCount();
            UpdateTypeFilterUi();

            // A filter or sort change is the same files in a new order, so already-generated
            // thumbnails are still valid and only the ordering of the remaining work changes.
            _thumbnailLoader.SetWorkingSet(_displayedItems, resetHandled: false);
        }

        // ---------- Selected file / tag editing ----------

        private MediaItem? _selectionAnchor;

        private void MediaTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is MediaItem item)
            {
                // Stops the click bubbling to the grid background, which would immediately
                // undo the selection being made here.
                e.Handled = true;

                bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

                if (shift && _selectionAnchor != null)
                {
                    int anchorIndex = _displayedItems.IndexOf(_selectionAnchor);
                    int clickedIndex = _displayedItems.IndexOf(item);
                    if (anchorIndex >= 0 && clickedIndex >= 0)
                    {
                        int start = Math.Min(anchorIndex, clickedIndex);
                        int end = Math.Max(anchorIndex, clickedIndex);
                        foreach (var i in _displayedItems) i.IsSelected = false;
                        for (int idx = start; idx <= end; idx++) _displayedItems[idx].IsSelected = true;
                    }
                    // anchor stays put, so repeated shift+clicks keep adjusting the range from the same start
                }
                else if (ctrl)
                {
                    item.IsSelected = !item.IsSelected;
                    _selectionAnchor = item;
                }
                else
                {
                    foreach (var i in _displayedItems) i.IsSelected = false;
                    item.IsSelected = true;
                    _selectionAnchor = item;
                }

                var primary = item.IsSelected ? item : _displayedItems.LastOrDefault(i => i.IsSelected);
                if (primary != null) ShowItemDetails(primary);
                else ClearSelection();

                UpdateSelectedCount();
            }
        }

        private void MediaTile_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is MediaItem item)
            {
                // Right-clicking an item that's already part of a multi-selection keeps the
                // whole selection intact (so the context menu acts on all of them), same as
                // Explorer. Right-clicking anything else replaces the selection with just it.
                if (!item.IsSelected)
                {
                    foreach (var i in _displayedItems) i.IsSelected = false;
                    item.IsSelected = true;
                    ShowItemDetails(item);
                }
            }
        }

        private void ShowItemDetails(MediaItem item)
        {
            _selectedItem = item;
            SelectedFileNameText.Text = item.FileName;
            SelectedFileMetaText.Text = $"{item.Kind} | {item.SizeDisplay} | Modified: {item.DateModified:g}";
            SelectedFileFolderText.Text = Path.GetDirectoryName(item.FullPath) ?? "";
            TagsEditBox.Text = ""; // Add Tags is input-only now; current tags are shown in Remove Tags below
            UpdateRemoveTagOptions();
            ShowPreview(item);
        }

        private List<MediaItem> GetSelectedItems()
        {
            var items = _displayedItems.Where(i => i.IsSelected).ToList();
            if (items.Count == 0 && _selectedItem != null) items.Add(_selectedItem);
            return items;
        }

        // ---------- Video / image preview ----------

        private async void ShowPreview(MediaItem item)
        {
            // Guards against a slower-to-parse video finishing after the user has already moved
            // on to another file and clobbering that later selection's preview.
            int requestId = ++_previewRequestId;

            // Reset any previous video playback before switching.
            _mediaPlayer?.Stop();
            VideoPositionSlider.Value = 0;
            _currentNativeWidth = 0;
            _currentNativeHeight = 0;

            // Audio goes through the same player as video - it just has no picture, so the
            // VideoView surface stays hidden and the artwork/placeholder image stays up
            // instead. Previously only Video took this path, so audio files silently showed a
            // static thumbnail and never played at all.
            bool playable = item.Kind is MediaKind.Video or MediaKind.Audio;

            if (playable && _libVLC != null && _mediaPlayer != null)
            {
                bool isAudio = item.Kind == MediaKind.Audio;

                PreviewImage.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;
                PreviewVideo.Visibility = isAudio ? Visibility.Collapsed : Visibility.Visible;
                if (isAudio) PreviewImage.Source = item.Thumbnail;

                PlayPauseButton.Visibility = Visibility.Visible;
                VideoPositionSlider.Visibility = Visibility.Visible;
                MuteButton.Visibility = Visibility.Visible;
                VolumeSlider.Visibility = Visibility.Visible;

                using var media = new Media(_libVLC, item.FullPath, FromType.FromPath);

                try
                {
                    await media.Parse(MediaParseOptions.ParseLocal);
                    var videoTrack = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
                    if (!isAudio && videoTrack.TrackType == TrackType.Video && videoTrack.Data.Video.Width > 0)
                    {
                        if (requestId != _previewRequestId) return; // superseded by a newer selection
                        _currentNativeWidth = (int)videoTrack.Data.Video.Width;
                        _currentNativeHeight = (int)videoTrack.Data.Video.Height;
                        UpdatePreviewScaleText();
                        UpdatePreviewHeight();
                    }
                }
                catch
                {
                    // Native size unknown; scale readout just shows "--".
                }

                if (requestId != _previewRequestId) return;

                _mediaPlayer.Media = media;
                ApplyMuteState(); // reapply mute/volume to the new media

                if (AutoplayCheckBox.IsChecked == true && PreviewExpander.IsExpanded)
                {
                    _mediaPlayer.Play();
                    PlayPauseButton.Content = "Pause";
                }
                else
                {
                    PlayPauseButton.Content = "Play";
                }
            }
            else
            {
                PreviewVideo.Visibility = Visibility.Collapsed;
                PlayPauseButton.Visibility = Visibility.Collapsed;
                VideoPositionSlider.Visibility = Visibility.Collapsed;
                MuteButton.Visibility = Visibility.Collapsed;
                VolumeSlider.Visibility = Visibility.Collapsed;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewImage.Source = item.Thumbnail;

                if (item.Kind == MediaKind.Image)
                {
                    var (w, h) = GetNativeImageSize(item.FullPath);
                    _currentNativeWidth = w;
                    _currentNativeHeight = h;
                }
            }

            UpdatePreviewScaleText();
            UpdatePreviewHeight();
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                PlayPauseButton.Content = "Play";
            }
            else
            {
                _mediaPlayer.Play();
                PlayPauseButton.Content = "Pause";
            }
        }

        private void PreviewExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                PlayPauseButton.Content = "Play";
            }
        }

        private void PreviewExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null || _mediaPlayer.Media == null) return; // nothing loaded to resume
            if (AutoplayCheckBox.IsChecked == true && !_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Play();
                PlayPauseButton.Content = "Pause";
            }
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _isMuted = !_isMuted;
            ApplyMuteState();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer == null) return;
            _lastVolume = (int)e.NewValue;
            if (!_isMuted)
            {
                _mediaPlayer.Volume = _lastVolume;
            }
        }

        // LibVLCSharp's MediaPlayer.Mute property is unreliable (doesn't consistently
        // take effect, and fights with the volume slider), so mute is tracked ourselves
        // and applied by zeroing/restoring the actual volume.
        private void ApplyMuteState()
        {
            if (_mediaPlayer == null) return;
            _mediaPlayer.Volume = _isMuted ? 0 : _lastVolume;
            MuteButton.Content = _isMuted ? "🔇" : "🔊";
            VolumeSlider.IsEnabled = !_isMuted;
        }

        // The scrub bar used to rely on WPF's built-in IsMoveToPointEnabled click-to-position
        // behavior, which only reliably jumps when the click lands exactly on the track background
        // and not the thumb - clicking near the thumb (or WPF just losing the race on a quick click)
        // silently did nothing. Computing the click position ourselves on every down/move event fixes
        // that: the slider's Value always matches exactly where the mouse is, whether it's a single
        // click or a drag, so the seek on release is always accurate.
        private void VideoSlider_DragStart(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = true;
            UpdateSliderValueFromMouseX(e.GetPosition(VideoPositionSlider).X);
        }

        private void VideoSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSlider && e.LeftButton == MouseButtonState.Pressed)
                UpdateSliderValueFromMouseX(e.GetPosition(VideoPositionSlider).X);
        }

        private void UpdateSliderValueFromMouseX(double x)
        {
            if (VideoPositionSlider.ActualWidth <= 0) return;
            double fraction = x / VideoPositionSlider.ActualWidth;
            VideoPositionSlider.Value = Math.Clamp(fraction, VideoPositionSlider.Minimum, VideoPositionSlider.Maximum);
        }

        private void VideoSlider_DragEnd(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSlider = false;
            if (_mediaPlayer != null && _mediaPlayer.Media != null)
                _mediaPlayer.Position = (float)VideoPositionSlider.Value;
        }

        private void ClearSelection()
        {
            _selectedItem = null;
            SelectedFileNameText.Text = "";
            SelectedFileMetaText.Text = "";
            SelectedFileFolderText.Text = "";
            TagsEditBox.Text = "";
            _removeTagOptions.Clear();
            NoRemovableTagsText.Visibility = Visibility.Visible;
            RemoveTagsButton.IsEnabled = false;

            _mediaPlayer?.Stop();
            VideoPositionSlider.Value = 0;
            PreviewVideo.Visibility = Visibility.Collapsed;
            PlayPauseButton.Visibility = Visibility.Collapsed;
            VideoPositionSlider.Visibility = Visibility.Collapsed;
            MuteButton.Visibility = Visibility.Collapsed;
            VolumeSlider.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewImage.Source = null;

            _currentNativeWidth = 0;
            _currentNativeHeight = 0;
            _previewExpandedToNative = false;
            UpdatePreviewScaleText();
            UpdatePreviewHeight();
        }

        // ---------- Tag editing + autocomplete ----------

        private void TagsEditBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTagSuggestions(TagsEditBox, TagSuggestionPopup, TagSuggestionList);
        }

        /// <summary>Rebuilds the "Remove Tags" checklist for whatever is currently selected: one
        /// row per tag present on at least one selected file, each starting unchecked. The count
        /// next to each tells you how many of the selected files actually have it.</summary>
        private void UpdateRemoveTagOptions()
        {
            var selected = GetSelectedItems();
            _removeTagOptions.Clear();

            if (selected.Count == 0)
            {
                NoRemovableTagsText.Visibility = Visibility.Visible;
                RemoveTagsButton.IsEnabled = false;
                return;
            }

            var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in selected)
                foreach (var tag in item.Tags)
                    tagCounts[tag] = tagCounts.TryGetValue(tag, out var c) ? c + 1 : 1;

            foreach (var kvp in tagCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                _removeTagOptions.Add(new RemoveTagOption(kvp.Key)
                {
                    PresentCount = kvp.Value,
                    TotalSelected = selected.Count,
                    IsChecked = false
                });
            }

            NoRemovableTagsText.Visibility = _removeTagOptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RemoveTagsButton.IsEnabled = _removeTagOptions.Count > 0;
        }

        /// <summary>Shared tag-autocomplete logic: works against any text box/popup/listbox trio, so
        /// both the preview pane's tag box and the bulk "Add/Change Tags" dialog get the same behavior.</summary>
        private void UpdateTagSuggestions(TextBox box, Popup popup, ListBox list)
        {
            var text = box.Text ?? "";
            var tokens = text.Split(' ');
            var lastToken = tokens.Length > 0 ? tokens[^1] : "";

            if (string.IsNullOrWhiteSpace(lastToken))
            {
                popup.IsOpen = false;
                return;
            }

            var existingTokens = tokens.Take(tokens.Length - 1)
                .Where(t => t.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The candidate pool is a union of whichever sources are ticked. Autocomplete
            // needs no rebuild when the checkboxes change - it just reads their current
            // state fresh on the next keystroke.
            var candidates = Enumerable.Empty<string>();
            if (AutocompleteFromFolderBox.IsChecked == true)
                candidates = candidates.Concat(_tagOptions.Select(o => o.Tag));
            if (AutocompleteFromLibraryBox.IsChecked == true)
                candidates = candidates.Concat(Settings.TagLibrary);

            var matches = candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => t.Contains(lastToken, StringComparison.OrdinalIgnoreCase)
                            && !existingTokens.Contains(t))
                // An exact match deliberately stays in the list. Dropping it meant that typing
                // "Dan" in full made the "Dan" option vanish while "Daniel" remained, which
                // reads as the tag not existing - and leaves no way to confirm the exact one
                // by clicking. It is still the first entry, being a prefix match of itself.
                .OrderBy(t => t.StartsWith(lastToken, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            if (matches.Count == 0)
            {
                popup.IsOpen = false;
                return;
            }

            list.ItemsSource = matches;
            popup.IsOpen = true;
        }

        private void AutocompleteSource_Changed(object sender, RoutedEventArgs e)
        {
            // AutocompleteFromFolderBox has IsChecked="True" in XAML, which fires this
            // Checked event during InitializeComponent() itself - while the parser is still
            // working through the file, before AutocompleteFromLibraryBox (declared just after
            // it) has been created. Without this guard, the very first line below throws a
            // NullReferenceException on every launch.
            if (!_initialized) return;

            // Applied live (autocomplete just reads these on the next keystroke) and
            // persisted the same way IncludeSubfolders and the type filter are - written to
            // Settings on exit rather than on every click, since there is nothing else here
            // that needs saving immediately.
            Settings.AutocompleteFromFolder = AutocompleteFromFolderBox.IsChecked == true;
            Settings.AutocompleteFromLibrary = AutocompleteFromLibraryBox.IsChecked == true;
        }

        private void TagLibrary_Click(object sender, RoutedEventArgs e)
        {
            var window = new TagLibraryWindow(this) { Owner = this };
            window.ShowDialog();
        }

        /// <summary>The distinct tags in use across the currently loaded folder(s), for the
        /// Tag Library window's Add page. Deliberately not _tagOptions (which also carries the
        /// synthetic "No tags" entry, meaningless outside the filter panel).</summary>
        internal List<string> GetFolderTags() =>
            _allItems.SelectMany(i => i.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private void ApplySuggestion(TextBox box, Popup popup, string tag)
        {
            var text = box.Text ?? "";
            var tokens = text.Split(' ').ToList();
            if (tokens.Count > 0) tokens[^1] = tag;
            else tokens.Add(tag);

            box.Text = string.Join(" ", tokens) + " ";
            box.CaretIndex = box.Text.Length;
            popup.IsOpen = false;
            box.Focus();
        }

        private void TagSuggestion_Click(object sender, MouseButtonEventArgs e)
        {
            if (TagSuggestionList.SelectedItem is string tag) ApplySuggestion(TagsEditBox, TagSuggestionPopup, tag);
        }

        private void TagSuggestionList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && TagSuggestionList.SelectedItem is string tag)
            {
                ApplySuggestion(TagsEditBox, TagSuggestionPopup, tag);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                TagSuggestionPopup.IsOpen = false;
                TagsEditBox.Focus();
                e.Handled = true;
                return;
            }

            // Arrowing up off the top goes back to typing rather than sticking on the first
            // suggestion, so the list is never something you get stuck inside.
            if (e.Key == Key.Up && TagSuggestionList.SelectedIndex <= 0)
            {
                TagsEditBox.Focus();
                TagsEditBox.CaretIndex = TagsEditBox.Text?.Length ?? 0;
                e.Handled = true;
            }
        }

        private void TagsEditBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // A suggestion showing gets completed first, matching Tab, rather than Enter
                // immediately submitting and cutting off what was being typed. Enter again
                // once nothing is left to suggest submits the tags.
                var lastTyped = (TagsEditBox.Text ?? "").Split(' ').LastOrDefault() ?? "";

                if (TagSuggestionPopup.IsOpen
                    && TagSuggestionList.Items.Count > 0
                    && TagSuggestionList.Items[0] is string first
                    // An exact match is left in the list now, so completing to it would replace
                    // the text with itself and swallow the keypress. Submit instead.
                    && !string.Equals(first, lastTyped, StringComparison.OrdinalIgnoreCase))
                {
                    ApplySuggestion(TagsEditBox, TagSuggestionPopup, first);
                }
                else
                {
                    TagSuggestionPopup.IsOpen = false;
                    AddTags_Click(sender, new RoutedEventArgs());
                }

                e.Handled = true;
                return;
            }

            if (!TagSuggestionPopup.IsOpen) return;

            if (e.Key == Key.Down)
            {
                if (TagSuggestionList.Items.Count > 0)
                {
                    TagSuggestionList.SelectedIndex = 0;
                    TagSuggestionList.Focus();
                    if (TagSuggestionList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem lbi)
                        lbi.Focus();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                TagSuggestionPopup.IsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                if (TagSuggestionList.Items.Count > 0 && TagSuggestionList.Items[0] is string first)
                {
                    ApplySuggestion(TagsEditBox, TagSuggestionPopup, first);
                    e.Handled = true;
                }
            }
        }

        /// <summary>Add Tags always merges the typed tag(s) into every selected file's existing
        /// tags (kept, deduped) - same behavior for one file or many, so there's no ambiguity about
        /// what this button does based on selection size. A confirmation is shown only for multiple
        /// files, since a single-file add is small and trivially undoable.</summary>
        private void AddTags_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems();
            if (items.Count == 0)
            {
                MessageBox.Show("Select a file first.", "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newTags = (TagsEditBox.Text ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            if (newTags.Count == 0)
            {
                MessageBox.Show("Enter at least one tag to add.", "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!ValidateTags(newTags)) return;

            if (items.Count > 1)
            {
                var confirm = MessageBox.Show(
                    $"Add tag(s) \"{string.Join(" ", newTags)}\" to all {items.Count} selected files?\n\nExisting tags on each file are kept; duplicates are ignored.",
                    "Confirm Tag Change", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            // Release the file lock first if a selected video is currently loaded in the preview.
            if (_selectedItem != null && (_selectedItem.Kind is MediaKind.Video or MediaKind.Audio) && items.Contains(_selectedItem))
            {
                _mediaPlayer?.Stop();
                if (_mediaPlayer != null) _mediaPlayer.Media = null;
            }

            ApplyTagChange(
                items,
                item => item.Tags.Concat(newTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                $"Add tag(s) {string.Join(" ", newTags)}");

            TagsEditBox.Text = ""; // input-only box; clear it once the add has been applied
        }

        /// <summary>Removes every checked tag in the Remove Tags list from all currently selected
        /// files (a no-op on files that don't have a given tag). Always confirms first, since
        /// removal loses data that Add Tags can't simply undo.</summary>
        private void RemoveTags_Click(object sender, RoutedEventArgs e)
        {
            var toRemove = _removeTagOptions.Where(o => o.IsChecked).Select(o => o.Tag).ToList();
            if (toRemove.Count == 0)
            {
                MessageBox.Show("Check at least one tag to remove.", "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var items = GetSelectedItems();
            if (items.Count == 0) return;

            var confirm = MessageBox.Show(
                $"Remove tag(s) \"{string.Join(" ", toRemove)}\" from {items.Count} selected file(s)?",
                "Confirm Tag Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            // Release the file lock first if a selected video is currently loaded in the preview.
            if (_selectedItem != null && (_selectedItem.Kind is MediaKind.Video or MediaKind.Audio) && items.Contains(_selectedItem))
            {
                _mediaPlayer?.Stop();
                if (_mediaPlayer != null) _mediaPlayer.Media = null;
            }

            ApplyTagChange(
                items,
                item => item.Tags.Where(t => !toRemove.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList(),
                $"Remove tag(s) {string.Join(" ", toRemove)}");
        }

        // ---------- Organize into folders ----------

        /// <summary>
        /// Runs a tag change over a set of files, collecting what actually happened so the
        /// caller can report collisions and refusals instead of them passing silently, and
        /// recording the batch as a single undo step.
        /// </summary>
        private void ApplyTagChange(
            List<MediaItem> items,
            Func<MediaItem, List<string>> tagsFor,
            string undoDescription)
        {
            int updated = 0, collided = 0;
            var errors = new List<string>();
            var renames = new List<FileRename>();

            foreach (var item in items)
            {
                var result = item.ApplyTags(tagsFor(item));

                if (!result.Succeeded)
                {
                    errors.Add($"{Path.GetFileName(result.PreviousPath)}: {result.Reason}");
                    continue;
                }

                if (result.Moved)
                {
                    renames.Add(new FileRename(result.PreviousPath, result.CurrentPath));
                    if (result.Kind == TagApplyResult.ResultKind.Collided) collided++;
                }

                updated++;
            }

            Undo.Record(undoDescription, renames);

            RebuildTagOptions();
            RefreshDisplayList();
            UpdateRemoveTagOptions();

            if (_selectedItem != null)
            {
                SelectedFileNameText.Text = _selectedItem.FileName;
                SelectedFileFolderText.Text = Path.GetDirectoryName(_selectedItem.FullPath) ?? "";
                if (items.Count == 1 && _selectedItem.Kind is MediaKind.Video or MediaKind.Audio) ShowPreview(_selectedItem);
            }

            var summary = $"{undoDescription} — {updated} file(s).";
            if (collided > 0) summary += $" {collided} renamed to avoid a name clash.";
            StatusText.Text = summary;

            if (collided > 0 || errors.Count > 0)
            {
                var message = summary;

                if (collided > 0)
                {
                    message += "\n\nA file already existed with the wanted name, so a number was " +
                               "added to keep both. Check those files if that wasn't intended.";
                }

                if (errors.Count > 0)
                    message += $"\n\nThese were left unchanged:\n{string.Join("\n", errors)}";

                MessageBox.Show(this, message, "TagCat",
                    MessageBoxButton.OK,
                    errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Rejects tags that cannot survive a round trip through a filename. Returns false
        /// after telling the user precisely which characters are the problem.
        /// </summary>
        internal bool ValidateTags(List<string> tags)
        {
            var bad = tags.Where(t => !TagParser.IsValidTag(t)).ToList();
            if (bad.Count == 0) return true;

            var detail = string.Join("\n",
                bad.Select(t => $"  {t}   (remove: {TagParser.DescribeInvalidCharacters(t)})"));

            MessageBox.Show(this,
                "These tags contain characters that can't be used:\n\n" + detail +
                "\n\nTags go into the filename, so they can't contain \\ / : * ? \" < > | " +
                "or the [ ] used to mark the tag block. Spaces separate one tag from the next.",
                "TagCat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // ---------- Keyboard shortcuts ----------

        /// <summary>
        /// Handled here rather than through Window.InputBindings: InputBindings are not part
        /// of the visual tree, so a Command binding using RelativeSource cannot resolve and
        /// fails silently. A key handler is both simpler and actually works.
        ///
        /// Shortcuts that would be destructive or disruptive are suppressed while a text box
        /// has focus, so typing a tag containing "a" does not select the whole library.
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Arrowing down into the tag suggestion list moves focus onto a ListBoxItem, not a
            // TextBox - so a plain "is the caret in a text box" check stopped being true and the
            // grid started stealing the arrow keys and Enter mid-tagging. Treating an open
            // suggestion popup as "still typing" keeps those keys where they belong.
            bool inTextBox = Keyboard.FocusedElement is TextBox || TagSuggestionPopup.IsOpen;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (!inTextBox && !ctrl && IsArrowKey(e.Key))
            {
                if (MoveSelectionByArrow(e.Key)) e.Handled = true;
                return;
            }

            // Guarded on inTextBox so the Add Tags box keeps its own Enter behaviour.
            if (!inTextBox && e.Key == Key.Enter && _selectedItem != null)
            {
                OpenWithPreferredMethod(_selectedItem);
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.Z && !inTextBox)
            {
                UndoLast();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.A && !inTextBox)
            {
                SelectAllDisplayed();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F5)
            {
                if (_currentFolders.Count > 0) LoadCurrentFolder();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F2 && GetSelectedItems().Count > 0)
            {
                TagsEditBox.Focus();
                TagsEditBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete && !inTextBox && GetSelectedItems().Count > 0)
            {
                DeleteSelected_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                // In the search box, Escape clears the search first; clearing the selection
                // as well would be more than was asked for.
                if (Keyboard.FocusedElement == SearchBox && !string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Clear();
                }
                else if (!inTextBox)
                {
                    foreach (var item in _displayedItems) item.IsSelected = false;
                    _selectionAnchor = null;
                    ClearSelection();
                    UpdateSelectedCount();
                }

                e.Handled = true;
            }
        }

        /// <summary>
        /// Shown beside "Selected File" so a multi-selection is visible without counting
        /// highlighted tiles - which matters because Add Tags and Remove Tags act on all of
        /// them, not just the one being previewed.
        /// </summary>
        private void UpdateSelectedCount()
        {
            int count = _displayedItems.Count(i => i.IsSelected);

            SelectedCountText.Text = count switch
            {
                0 => "",
                1 => "(1 file selected)",
                _ => $"({count} files selected)"
            };
        }

        /// <summary>
        /// Clicking empty space in the grid clears the selection, matching Explorer. The
        /// handler is on the ItemsControl background, so a click that lands on a tile is
        /// marked handled there and never reaches this.
        /// </summary>
        private void MediaBackground_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (GetSelectedItems().Count == 0) return;

            foreach (var item in _displayedItems) item.IsSelected = false;
            _selectionAnchor = null;
            ClearSelection();
            UpdateSelectedCount();
        }

        private void SelectAllDisplayed()
        {
            if (_displayedItems.Count == 0) return;

            foreach (var item in _displayedItems) item.IsSelected = true;

            ShowItemDetails(_displayedItems[^1]);
            UpdateRemoveTagOptions();
            UpdateSelectedCount();
            StatusText.Text = $"Selected {_displayedItems.Count} file(s).";
        }

        // ---------- Undo ----------

        internal UndoHistory Undo { get; } = new();

        private void UndoLast()
        {
            if (!Undo.CanUndo)
            {
                StatusText.Text = "Nothing to undo.";
                return;
            }

            // A loaded video holds its file open, which would block moving it back.
            _mediaPlayer?.Stop();
            if (_mediaPlayer != null) _mediaPlayer.Media = null;

            var outcome = Undo.UndoLast();

            if (_currentFolders.Count > 0) LoadCurrentFolder();
            StatusText.Text = outcome.Summary;

            if (outcome.Failures.Count > 0)
            {
                MessageBox.Show(this,
                    $"{outcome.Summary}\n\n{string.Join("\n", outcome.Failures)}",
                    "Undo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- Tile sizing ----------

        /// <summary>Room under the thumbnail for the filename and tag lines, plus padding
        /// and border. Fixed rather than scaled, because the text size does not change with
        /// the thumbnail, and the virtualizing panel needs a uniform cell.</summary>
        private const double TileTextHeight = 66;

        /// <summary>The tile width and thumbnail height the app ships with. The slider scales
        /// from these, so the tile keeps its proportions instead of only growing downwards.</summary>
        private const double BaseTileWidth = 150;
        private const double BaseThumbnailHeight = AppSettings.DefaultThumbnailHeight;

        /// <summary>Tile width, scaled in step with the thumbnail so the shape is preserved.</summary>
        public double TileWidth => Math.Round(BaseTileWidth * (ThumbnailHeight / BaseThumbnailHeight));

        /// <summary>Outer cell sizes the panel lays out: tile plus its 6px margin each side.</summary>
        public double TileHeight => TileContentHeight + 12;
        public double TileCellWidth => TileWidth + 12;

        /// <summary>
        /// Smallest the centre thumbnail pane may shrink to: one tile column plus the
        /// scrollbar and the grid's own outer margins (10px each side). Bound to from
        /// MainWindow.xaml's MinWidth so the right splitter can never drag the pane smaller
        /// than this and let the preview panel cover the file list entirely. Tracks the
        /// actual scrollbar width from Windows rather than a guessed constant.
        /// </summary>
        public double MinThumbnailPaneWidth => TileCellWidth + SystemParameters.VerticalScrollBarWidth + 20;

        public double TileContentHeight => ThumbnailHeight + TileTextHeight;

        // ---------- Settings ----------

        /// <summary>Exposed for the Settings window's About section.</summary>
        internal static string Version => AppVersion;

        private double _thumbnailHeight = AppSettings.DefaultThumbnailHeight;

        /// <summary>
        /// Height of each tile's thumbnail image. Bound to by the file grid, so assigning
        /// this resizes the grid live. The underlying bitmaps are decoded at a fixed size,
        /// so growing them past that softens the image rather than revealing more detail -
        /// which is why the slider stops where it does.
        /// </summary>
        public double ThumbnailHeight
        {
            get => _thumbnailHeight;
            set
            {
                if (Math.Abs(_thumbnailHeight - value) < 0.5) return;
                _thumbnailHeight = value;
                OnPropertyChanged(nameof(ThumbnailHeight));
                OnPropertyChanged(nameof(TileHeight));
                OnPropertyChanged(nameof(TileContentHeight));
                OnPropertyChanged(nameof(TileWidth));
                OnPropertyChanged(nameof(TileCellWidth));
                OnPropertyChanged(nameof(MinThumbnailPaneWidth));
            }
        }

        /// <summary>Mirrors the mute button, so Settings and the player agree.</summary>
        internal bool IsPreviewMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted == value) return;
                _isMuted = value;
                ApplyMuteState();
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow(this, openedFromDuplicateFinder: false) { Owner = this };
            window.ShowDialog();
        }

        /// <summary>
        /// Swaps the items panel between the virtualizing one and a plain WrapPanel. Built in
        /// code rather than as two XAML templates because ItemsPanelTemplate has to be sealed
        /// before assignment, and doing it here keeps the two variants side by side.
        /// </summary>
        /// <summary>Tracks which mode is currently applied, so re-running this when nothing
        /// changed (e.g. every Settings Cancel, which reverts the whole settings object
        /// unconditionally) is a no-op instead of tearing down and rebuilding the panel.</summary>
        private bool? _appliedVirtualization;

        internal void ApplyVirtualizationSetting()
        {
            if (_appliedVirtualization == Settings.UseVirtualization) return;
            _appliedVirtualization = Settings.UseVirtualization;

            var template = new ItemsPanelTemplate();

            if (Settings.UseVirtualization)
            {
                var factory = new FrameworkElementFactory(typeof(Controls.VirtualizingWrapPanel));
                factory.SetBinding(Controls.VirtualizingWrapPanel.ItemWidthProperty,
                    new System.Windows.Data.Binding(nameof(TileCellWidth)) { Source = this });
                factory.SetBinding(Controls.VirtualizingWrapPanel.ItemHeightProperty,
                    new System.Windows.Data.Binding(nameof(TileHeight)) { Source = this });
                template.VisualTree = factory;
            }
            else
            {
                var factory = new FrameworkElementFactory(typeof(WrapPanel));
                factory.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
                template.VisualTree = factory;
            }

            template.Seal();
            MediaItemsControl.ItemsPanel = template;

            // The panel is recreated by the swap, so the subscription has to be re-made. It is
            // only available once layout has produced it, hence the dispatcher hop.
            Dispatcher.BeginInvoke(new Action(HookVisibleRangeReporting),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // A plain WrapPanel has no IScrollInfo, so the ScrollViewer must go back to
            // scrolling by pixel over the whole realized content. The ScrollViewer now lives
            // inside the ItemsControl's template, so it has to be looked up rather than
            // referenced by name, and the template may not be applied yet on first call.
            MediaItemsControl.ApplyTemplate();
            if (MediaItemsControl.Template?.FindName("MediaScrollViewer", MediaItemsControl)
                is ScrollViewer scrollViewer)
            {
                scrollViewer.CanContentScroll = Settings.UseVirtualization;
            }
        }

        /// <summary>
        /// Connects the panel's visible-range reporting to the thumbnail loader. With
        /// virtualization off there is no such panel, so thumbnails simply load in display
        /// order top-down - still far better than the storage order this replaced.
        /// </summary>
        private void HookVisibleRangeReporting()
        {
            var panel = FindVisualChild<Controls.VirtualizingWrapPanel>(MediaItemsControl);
            if (panel == null)
            {
                _thumbnailLoader.SetPriorityRange(-1, -1);
                return;
            }

            panel.VisibleRangeChanged -= Panel_VisibleRangeChanged;
            panel.VisibleRangeChanged += Panel_VisibleRangeChanged;
        }

        private void Panel_VisibleRangeChanged(object? sender, Controls.VisibleRangeEventArgs e) =>
            _thumbnailLoader.SetPriorityRange(e.First, e.Last);

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;

                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }

            return null;
        }

        /// <summary>Applies the current retention setting to an open finder, if there is one.</summary>
        internal void ApplyFingerprintRetentionToOpenFinder()
        {
            if (_duplicateFinder == null) return;
            _duplicateFinder.RetainFingerprints = Settings.RetainFingerprints;
            _duplicateFinder.DatabasePath = Settings.FingerprintDatabasePath;
            _duplicateFinder.UseViewer = Settings.OpenDuplicatesInViewer;
            _duplicateFinder.ExcludedScanFolders = Settings.ExcludedScanFolders;
            _duplicateFinder.ExcludedScanFiles = Settings.ExcludedScanFiles;
            _duplicateFinder.ApplyFingerprintRetention();
        }

        /// <summary>
        /// Applies "keep thumbnails between sessions" and the cache folder, live. Called at
        /// startup and again whenever either changes in Settings. Off, or a folder change,
        /// swaps in a fresh ThumbnailCacheStore rather than reusing the old one, so the loader
        /// is never left pointing at a database that no longer matches what Settings says.
        /// </summary>
        /// <summary>
        /// A thumbnail failed on a format that needs a Microsoft Store extension. Raised at
        /// most once per file type per session by the loader, so this can prompt directly
        /// without becoming a nuisance on a folder full of them.
        ///
        /// Tagging, renaming, filtering and playback all still work on these files - only the
        /// thumbnail is missing - so this is worded as an offer, not an error.
        /// </summary>
        private void OnCodecSuggested(string filePath)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var name = CodecHelper.DisplayNameFor(filePath);
                if (name == null) return;

                var extension = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
                var note = CodecHelper.NoteFor(filePath);

                var message =
                    $"Windows can't generate thumbnails for {extension} files on this computer - " +
                    $"it needs Microsoft's free \"{name}\" package from the Store.\n\n" +
                    "Everything else still works: these files can be tagged, renamed, filtered and " +
                    "played as normal. Only the thumbnail is missing.\n\n" +
                    (string.IsNullOrEmpty(note) ? "" : note + "\n\n") +
                    "Open the Microsoft Store to install it?";

                var answer = MessageBox.Show(this, message, "Missing image support",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes) CodecHelper.OpenStorePageFor(filePath);
            });
        }

        internal void ApplyThumbnailCacheSetting()
        {
            if (!Settings.RetainThumbnails)
            {
                _thumbnailCache = null;
                _thumbnailLoader.Cache = null;
                return;
            }

            _thumbnailCache = new ThumbnailCacheStore(Settings.ThumbnailDatabasePath);
            _ = _thumbnailCache.InitializeAsync(); // fire-and-forget: a race with the first
                                                    // lookup just looks like a cache miss
            _thumbnailLoader.Cache = _thumbnailCache;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ---------- Duplicate finder ----------

        /// <summary>The open duplicate finder, if any. Kept so clicking the button a second
        /// time surfaces the existing window (and any scan running in it) instead of opening
        /// a second copy that would fight the first over the same fingerprint cache.</summary>
        private VideoDedupe.DuplicateFinderWindow? _duplicateFinder;

        private void DuplicateFinder_Click(object sender, RoutedEventArgs e)
        {
            if (_duplicateFinder != null)
            {
                if (_duplicateFinder.WindowState == WindowState.Minimized)
                    _duplicateFinder.WindowState = WindowState.Normal;
                _duplicateFinder.Activate();
                return;
            }

            // Opened as an independent window by default so it can sit alongside TagCat
            // and you can carry on tagging while a long scan runs; Settings can switch it to a
            // modal popup instead (see below).
            // Seeded with whatever is actually open here - the folder being browsed if folder
            // browsing is active, otherwise the loaded selection - so scanning the library you
            // are already looking at doesn't mean picking the same folders a second time.
            var seedFolders = _browsingFolder is not null && Directory.Exists(_browsingFolder)
                ? new List<string> { _browsingFolder }
                : _currentFolders;

            _duplicateFinder = new VideoDedupe.DuplicateFinderWindow(seedFolders)
            {
                RetainFingerprints = Settings.RetainFingerprints,
                DatabasePath = Settings.FingerprintDatabasePath,
                ExcludedScanFolders = Settings.ExcludedScanFolders,
                ExcludedScanFiles = Settings.ExcludedScanFiles,

                // A callback rather than a reference, so the finder needs no knowledge of
                // what is hosting it.
                OpenSettings = () =>
                {
                    var window = new SettingsWindow(this, openedFromDuplicateFinder: true) { Owner = _duplicateFinder };
                    window.ShowDialog();
                },

                UseViewer = Settings.OpenDuplicatesInViewer,
                OpenInViewer = (groupFiles, clicked) => OpenViewerForPaths(groupFiles, clicked, _duplicateFinder),
                OpenInMediaTagger = (folder, file) => OpenFolderAndHighlightAsync(folder, file),

                AddExcludedFolderPermanently = folder =>
                {
                    if (Settings.ExcludedScanFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
                    Settings.ExcludedScanFolders = Settings.ExcludedScanFolders.Append(folder).ToArray();
                    SaveSettings();
                    ApplyFingerprintRetentionToOpenFinder();
                },
                AddExcludedFilePermanently = file =>
                {
                    if (Settings.ExcludedScanFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) return;
                    Settings.ExcludedScanFiles = Settings.ExcludedScanFiles.Append(file).ToArray();
                    SaveSettings();
                    ApplyFingerprintRetentionToOpenFinder();
                }
            };
            _duplicateFinder.ApplyFingerprintRetention();
            _duplicateFinder.Closed += (_, _) => _duplicateFinder = null;

            if (Settings.OpenDuplicateFinderAsPopup)
            {
                // Owner + ShowDialog: stays in front and blocks TagCat behind it.
                // ShowInTaskbar off so it doesn't also get its own taskbar button - as a modal
                // popup it isn't independently switchable, so a separate entry would just be a
                // dead end. Left on for the separate-window mode, where it genuinely is.
                _duplicateFinder.Owner = this;
                _duplicateFinder.ShowInTaskbar = false;
                _duplicateFinder.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                // ShowDialog does not return until it closes, so nothing may follow this.
                _duplicateFinder.ShowDialog();
            }
            else
            {
                _duplicateFinder.Show();
            }
        }

        private void Organize_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFolders.Count == 0) return;
            if (_displayedItems.Count == 0)
            {
                MessageBox.Show("No files are currently visible to organize.", "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selectedTags = _tagOptions.Where(o => o.IsChecked).Select(o => o.Tag).ToList();
            if (selectedTags.Count == 0)
            {
                MessageBox.Show("Check at least one tag on the left to organize by.", "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                SelectedPath = _currentFolders.FirstOrDefault() ?? "",
                Description = "Choose the destination root folder (a subfolder will be created inside it, named after the selected tag(s))."
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var confirm = MessageBox.Show(
                $"Move {_displayedItems.Count} visible file(s) into:\n{dialog.SelectedPath}\\{string.Join(" ", selectedTags)}\n\nThis moves files on disk. Continue?",
                "Confirm Organize", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _mediaPlayer?.Stop();
            if (_mediaPlayer != null) _mediaPlayer.Media = null; // release any file lock before moving files on disk

            int moved = LibraryService.Organize(_displayedItems.ToList(), dialog.SelectedPath, OrganizeBy.SelectedTags, selectedTags);

            MessageBox.Show($"Moved {moved} file(s).", "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);

            ClearSelection();
            LoadCurrentFolder();
        }

        private void AddTagsSelected_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems();
            if (items.Count == 0) return;

            string title = items.Count == 1 ? $"Add tags to \"{items[0].FileName}\"" : $"Add tags to {items.Count} files";
            var input = PromptForTags(title, "Enter tag(s) to add (space separated). Existing tags are kept; duplicates are ignored.");
            if (string.IsNullOrWhiteSpace(input)) return;

            var newTags = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (newTags.Count == 0) return;
            if (!ValidateTags(newTags)) return;

            // Release the file lock first if a selected video is currently loaded in the preview.
            if (_selectedItem != null && (_selectedItem.Kind is MediaKind.Video or MediaKind.Audio) && items.Contains(_selectedItem))
            {
                _mediaPlayer?.Stop();
                if (_mediaPlayer != null) _mediaPlayer.Media = null;
            }

            ApplyTagChange(
                items,
                item => item.Tags.Concat(newTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                $"Add tag(s) {string.Join(" ", newTags)}");
        }

        /// <summary>Modal tag-entry dialog (WPF has no built-in InputBox) with the same
        /// autocomplete-from-existing-tags behavior as the preview pane's tag box. Returns null if cancelled.</summary>
        private string? PromptForTags(string title, string prompt)
        {
            var promptText = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            var input = new TextBox { Margin = new Thickness(0, 0, 0, 12) };

            var suggestionList = new ListBox { BorderThickness = new Thickness(0) };
            var suggestionBorder = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                MaxHeight = 150,
                MinWidth = 150,
                Child = suggestionList
            };
            var suggestionPopup = new Popup
            {
                PlacementTarget = input,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = suggestionBorder
            };

            input.TextChanged += (s, e) => UpdateTagSuggestions(input, suggestionPopup, suggestionList);
            input.PreviewKeyDown += (s, e) =>
            {
                if (!suggestionPopup.IsOpen) return;

                if (e.Key == Key.Down)
                {
                    if (suggestionList.Items.Count > 0)
                    {
                        suggestionList.SelectedIndex = 0;
                        suggestionList.Focus();
                        if (suggestionList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem lbi)
                            lbi.Focus();
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    suggestionPopup.IsOpen = false;
                    e.Handled = true;
                }
                else if (e.Key == Key.Tab)
                {
                    if (suggestionList.Items.Count > 0 && suggestionList.Items[0] is string first)
                    {
                        ApplySuggestion(input, suggestionPopup, first);
                        e.Handled = true;
                    }
                }
            };
            suggestionList.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (suggestionList.SelectedItem is string tag) ApplySuggestion(input, suggestionPopup, tag);
            };
            suggestionList.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && suggestionList.SelectedItem is string tag)
                {
                    ApplySuggestion(input, suggestionPopup, tag);
                    e.Handled = true;
                }
            };

            // Popup renders in its own adorner layer when open, positioned relative to
            // PlacementTarget - it just needs to be somewhere in the visual tree.
            var inputHost = new Grid();
            inputHost.Children.Add(input);
            inputHost.Children.Add(suggestionPopup);

            // MinWidth is set explicitly because the shared implicit Button style carries
            // MinWidth 88, which would otherwise win over Width and stretch these.
            var okButton = new Button { Content = "OK", Width = 70, MinWidth = 0, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "Cancel", Width = 70, MinWidth = 0, IsCancel = true };
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(promptText);
            root.Children.Add(inputHost);
            root.Children.Add(buttonPanel);

            var window = new Window
            {
                Title = title,
                Width = 400,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Content = root
            };

            bool confirmed = false;
            okButton.Click += (s, e) => { confirmed = true; window.Close(); };
            cancelButton.Click += (s, e) => window.Close();
            window.Loaded += (s, e) => input.Focus();

            window.ShowDialog();
            return confirmed ? input.Text : null;
        }

        // ---------- Open / move / copy / delete (right-click menu) ----------

        private void OpenSelected_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems();
            foreach (var item in items)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Couldn't open {item.FileName}: {ex.Message}", "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OpenInViewer_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems();
            if (items.Count > 0) OpenInViewer(items[0]);
        }

        /// <summary>
        /// Double-click detection lives here rather than on a MouseDoubleClick handler, because
        /// that event belongs to Control and the tile is a Border, which isn't one. ClickCount
        /// on the plain mouse-down event is the equivalent, and it fires before MouseLeftButtonUp
        /// so the selection click still runs normally on the way through.
        /// </summary>
        private void MediaTile_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2) return;

            if (sender is FrameworkElement fe && fe.Tag is MediaItem item)
            {
                OpenWithPreferredMethod(item);
                e.Handled = true;
            }
        }

        /// <summary>Double-click and Enter both route through here, so the Settings preference
        /// applies to both. The right-click menu deliberately bypasses it, offering each option
        /// explicitly.</summary>
        private void OpenWithPreferredMethod(MediaItem item)
        {
            if (Settings.OpenFilesInViewer) OpenInViewer(item);
            else OpenWithSystemDefault(item);
        }

        private void OpenWithSystemDefault(MediaItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open {item.FileName}: {ex.Message}", "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Opens the viewer over the currently displayed list, so its next/previous navigation
        /// follows the same order and filtering as the grid rather than the raw folder.
        /// </summary>
        private void OpenInViewer(MediaItem item)
        {
            // The preview pane holds a file open; releasing it first keeps the viewer from
            // fighting it over the same file, and stops a later rename being blocked.
            _mediaPlayer?.Stop();

            var viewer = new MediaViewerWindow(_displayedItems, item) { Owner = this };

            // Follow the viewer's navigation, so closing it leaves the grid highlighting
            // whatever was last on screen rather than whatever was opened originally.
            viewer.CurrentItemChanged += viewerItem =>
            {
                int index = _displayedItems.IndexOf(viewerItem);
                if (index < 0) return;

                foreach (var i in _displayedItems) i.IsSelected = false;
                viewerItem.IsSelected = true;
                _selectionAnchor = viewerItem;
                _selectedItem = viewerItem;

                UpdateSelectedCount();
                ScrollItemIntoView(index);
            };

            viewer.ShowDialog();

            // The viewer changed the selection as it navigated; refresh the detail panel and
            // tag lists now it's closed, which was skipped while it was open to avoid the
            // preview pane loading files behind it.
            if (_selectedItem != null)
            {
                ShowItemDetails(_selectedItem);
                UpdateRemoveTagOptions();
            }
        }

        /// <summary>
        /// Opens the viewer over an arbitrary list of paths rather than the grid's own items -
        /// used by the Duplicate Finder, where the list is one duplicate group. MediaItems are
        /// built on the spot because these files may not be in the currently loaded library at
        /// all. No selection syncing here for the same reason: they may not exist in the grid.
        /// </summary>
        internal void OpenViewerForPaths(IReadOnlyList<string> paths, string startAt, Window? owner)
        {
            var items = paths.Where(File.Exists).Select(p => new MediaItem(p)).ToList();
            if (items.Count == 0) return;

            var start = items.FirstOrDefault(i => i.FullPath.Equals(startAt, StringComparison.OrdinalIgnoreCase))
                        ?? items[0];

            var viewer = new MediaViewerWindow(items, start) { Owner = owner ?? this };
            viewer.ShowDialog();
        }

        /// <summary>
        /// Loads a folder from the Duplicate Finder and selects the file that led there.
        ///
        /// Whether this is even reachable depends on the app's shutdown mode: with no
        /// ShutdownMode set, WPF defaults to OnLastWindowClose, and "Close Duplicate Finder
        /// when TagCat closes" is off by default - so closing this window does NOT
        /// close Duplicate Finder or end the process if that setting is off. Duplicate Finder
        /// can genuinely still be open with this window gone. A closed Window cannot be
        /// reshown (WPF throws), so the Duplicate Finder side checks IsHostWindowClosed,
        /// set below, before ever calling this rather than finding out the hard way.
        /// </summary>
        internal async Task OpenFolderAndHighlightAsync(string folder, string highlightPath)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();

            await SetCurrentFoldersAsync(new List<string> { folder });

            var target = _displayedItems.FirstOrDefault(i =>
                i.FullPath.Equals(highlightPath, StringComparison.OrdinalIgnoreCase));
            if (target == null) return;

            int index = _displayedItems.IndexOf(target);
            foreach (var item in _displayedItems) item.IsSelected = false;
            target.IsSelected = true;
            _selectedItem = target;

            ShowItemDetails(target);
            UpdateRemoveTagOptions();
            UpdateSelectedCount();
            ScrollItemIntoView(index);
        }

        private void OpenContainingFolder_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems();
            if (items.Count == 0) return;

            // If several selected files span multiple folders, open each distinct folder once
            // (with one representative file selected in it) rather than a window per file.
            var onePerFolder = items
                .GroupBy(i => Path.GetDirectoryName(i.FullPath) ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());

            foreach (var item in onePerFolder)
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Couldn't open the folder for {item.FileName}: {ex.Message}", "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void MoveSelected_Click(object sender, RoutedEventArgs e) => MoveOrCopySelected(FileOpMode.Move);
        private void CopySelected_Click(object sender, RoutedEventArgs e) => MoveOrCopySelected(FileOpMode.Copy);

        private void MoveOrCopySelected(FileOpMode mode)
        {
            var items = GetSelectedItems();
            if (items.Count == 0) return;

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                SelectedPath = _currentFolders.FirstOrDefault() ?? "",
                Description = mode == FileOpMode.Move
                    ? "Choose a folder to move the selected file(s) to."
                    : "Choose a folder to copy the selected file(s) to."
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            // Release the file lock first if a selected video is currently loaded in the preview.
            if (_selectedItem != null && (_selectedItem.Kind is MediaKind.Video or MediaKind.Audio) && items.Contains(_selectedItem))
            {
                _mediaPlayer?.Stop();
                if (_mediaPlayer != null) _mediaPlayer.Media = null;
            }

            try
            {
                int count = LibraryService.CopyOrMove(items, dialog.SelectedPath, mode);
                string verb = mode == FileOpMode.Move ? "Moved" : "Copied";
                MessageBox.Show($"{verb} {count} file(s).", "TagCat", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                string verb = mode == FileOpMode.Move ? "move" : "copy";
                MessageBox.Show($"Couldn't {verb} files: {ex.Message}", "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            ClearSelection();
            LoadCurrentFolder();
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems();
            if (items.Count == 0) return;

            string message = items.Count == 1
                ? $"Send \"{items[0].FileName}\" to the Recycle Bin?"
                : $"Send {items.Count} selected files to the Recycle Bin?";

            var confirm = MessageBox.Show(message, "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            if (_selectedItem != null && (_selectedItem.Kind is MediaKind.Video or MediaKind.Audio) && items.Contains(_selectedItem))
            {
                _mediaPlayer?.Stop();
                if (_mediaPlayer != null) _mediaPlayer.Media = null;
            }

            int deleted = 0;
            var errors = new List<string>();
            foreach (var item in items)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        item.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    deleted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.FileName}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
                MessageBox.Show(
                    $"Deleted {deleted} file(s). Some failed:\n{string.Join("\n", errors)}",
                    "TagCat", MessageBoxButton.OK, MessageBoxImage.Warning);

            ClearSelection();
            LoadCurrentFolder();
        }

        // ---------- File properties (right-click menu) ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string? lpVerb;
            public string? lpFile;
            public string? lpParameters;
            public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private const int SW_SHOW = 5;

        private void FileProperties_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems();
            if (items.Count == 0) return;

            if (items.Count == 1)
            {
                // A single file gets the real Windows Properties dialog (General/Details/Security tabs).
                var info = new SHELLEXECUTEINFO();
                info.cbSize = Marshal.SizeOf(info);
                info.lpVerb = "properties";
                info.lpFile = items[0].FullPath;
                info.nShow = SW_SHOW;
                info.fMask = SEE_MASK_INVOKEIDLIST;

                if (!ShellExecuteEx(ref info))
                    MessageBox.Show("Couldn't open the Properties dialog for this file.", "TagCat", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // The native dialog only targets one file at a time; for a multi-selection, show a
            // quick combined summary instead of popping open a separate dialog per file.
            long totalBytes = items.Sum(i => i.SizeInBytes);
            var byExt = items.GroupBy(i => i.Extension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}: {g.Count()}");

            string message = $"{items.Count} files selected\nTotal size: {MediaItem.FormatSize(totalBytes)}\n\n{string.Join("\n", byExt)}";
            MessageBox.Show(message, "Properties", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------- Version / changelog ----------


        /// <summary>
        /// The changelog source is hard-wrapped so it stays readable in the editor, with
        /// continuation lines indented under their bullet. Rendered as-is in a TextBlock that
        /// also wraps, every entry broke twice - once at the author's column and again at the
        /// window's - producing a ragged left edge and stray one-word lines. Joining each
        /// bullet back into a single logical line hands all the wrapping to the TextBlock,
        /// which then breaks cleanly at the window width whatever that happens to be.
        /// </summary>
        private static string ReflowChangelog(string text)
        {
            var output = new System.Text.StringBuilder();
            var lines = text.Replace("\r\n", "\n").Split('\n');

            foreach (var line in lines)
            {
                // A continuation is an indented line that does not itself start a bullet.
                bool isContinuation =
                    line.StartsWith("   ", StringComparison.Ordinal) &&
                    !line.TrimStart().StartsWith("- ", StringComparison.Ordinal) &&
                    line.Trim().Length > 0 &&
                    output.Length > 0;

                if (isContinuation)
                {
                    // Re-join with a single space, undoing the author's line break.
                    if (output.Length > 0 && output[^1] != ' ') output.Append(' ');
                    output.Append(line.Trim());
                }
                else
                {
                    if (output.Length > 0) output.Append('\n');
                    output.Append(line.TrimEnd());
                }
            }

            return output.ToString();
        }

        /// <summary>Exposed so the Settings window can show the same changelog, owned by
        /// whichever window asked for it (otherwise it would sit behind the dialog).</summary>
        /// <summary>
        /// Both changelogs share this: the text is passed in rather than each having its own
        /// near-duplicate window-building code. The reflow/line-height handling exists because
        /// the source text is hard-wrapped for readability in the source file, and a TextBlock
        /// that also wraps would otherwise break every entry twice.
        /// </summary>
        private void ShowChangelogWindow(Window owner, string title, string changelogText)
        {
            var window = new Window
            {
                Title = title,
                Width = 660,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                Content = new ScrollViewer
                {
                    Padding = new Thickness(16),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new TextBlock
                    {
                        Text = ReflowChangelog(changelogText),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 17,
                        LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                    }
                }
            };
            window.ShowDialog();
        }

        internal void ShowTagCatChangelog(Window owner) =>
            ShowChangelogWindow(owner, $"TagCat — Changelog (current: v{AppVersion})", TagCatChangelogText);

        internal void ShowDuplicateFinderChangelog(Window owner) =>
            ShowChangelogWindow(owner, $"Duplicate Finder — Changelog (current: v{VideoDedupe.DuplicateFinderWindow.DedupeVersion})", DuplicateFinderChangelogText);

        /// <summary>
        /// Split from what used to be one combined changelog, on request: a release that only
        /// touched the Duplicate Finder has no business appearing in TagCat's own log,
        /// and vice versa. Historical entries were split the same way, based on which product
        /// each bullet actually described - most were already labelled "Duplicate Finder:" at
        /// the time, and a handful of older ones that clearly described DF behaviour without
        /// that label were moved over by hand. A few entries from the era before the two
        /// products were fully separated still legitimately mention both.
        /// </summary>
        private const string TagCatChangelogText = """
TagCat — Changelog

v0.59b
 - Fixed the update check never finding a release. It only recognised tags like "v0.58.19",
   but real release tags carry a name prefix - "TagCat-v0.58.19" - which it failed to parse,
   so it reported a comparison error instead of an update. Any prefix before the version
   number is now ignored.

v0.59a
 - Added LICENSE (MIT for TagCat's own code), THIRD-PARTY-NOTICES.txt covering LibVLC and
   the other bundled components, and a .gitignore so build output stays out of the repo.

v0.59
 - Added an update check. A "Check for updates" button in Settings > About, plus an optional
   check at startup. The first run asks whether you want the startup check before it ever
   contacts GitHub; it's off unless you say yes, and can be changed in Settings afterwards.
 - It only reads the latest version number from GitHub - nothing is downloaded or installed
   automatically. If an update exists it offers to open the download page.
 - Declining an update at startup won't ask again for that same version.

v0.57
 - HEIC and HEIF files that Windows can't generate thumbnails for now prompt once, offering
   to open the Microsoft Store to the free package that fixes it. Tagging, renaming,
   filtering and playback already worked on these files - only the thumbnail was missing -
   so the prompt says so rather than reading like an error. Shown once per file type per
   session, not once per file.

v0.56a
 - Fixed the launcher RUN.bat in the zip root still looking for a "MediaTagger" folder,
   which no longer exists after the rename - double-clicking it failed immediately.

v0.56
 - Renamed from Media Tagger to TagCat - the old name was already in use by another app.
   The program file is now TagCat.exe.
 - Settings now live in %AppData%\TagCat instead of %AppData%\MediaTagger. Existing
   settings are copied across automatically the first time this version runs; the old
   folder is left in place rather than deleted, so nothing is lost if anything goes wrong.
 - The thumbnail and fingerprint caches also moved to %LocalAppData%\TagCat. These are
   NOT copied over - they rebuild themselves on demand, so the first scan and the first
   look at a folder will be slower than usual, once. The old cache folders can be deleted
   by hand whenever you like.
 - Changelog entries from before this version still say "Media Tagger", since that is what
   it was called at the time.

v0.55
 - Fixed the "Loop" checkbox in the popup viewer's video bar rendering faint and dark -
   it's now drawn explicitly instead of relying on the system theme's default checkbox
   style, which assumes a light background.
 - Installer: the Start Menu shortcut now goes directly into the Start Menu instead of
   its own subfolder, and the separate "Uninstall" shortcut is gone (still available via
   right-click, or Add/Remove Programs). The now-unnecessary "Select Start Menu Folder"
   page no longer appears during setup.
 - Installer: now asks for confirmation before downloading .NET and/or VLC, naming
   whichever is actually needed. Declining still installs Media Tagger - it just skips
   the download, and Media Tagger explains what to do about VLC when it's next opened.

v0.54d
 - Fixed MAKE-INSTALLER-PACKAGE.bat still producing an ~80MB installer package. It was
   only deleting libvlc.dll/libvlccore.dll/plugins from the published app's root folder,
   but LibVLC's NuGet package actually places its files in their own "libvlc" subfolder
   - confirmed by checking the real published output, rather than assumed this time. That
   subfolder is now deleted too. The script also reports the package's final size at the
   end, so this is visible immediately on the next run.

v0.54c
 - Fixed a real compile error in MediaTagger.iss: the LibVLC extraction step's inline
   PowerShell command used { } script-block syntax, which Inno Setup's compiler tried to
   read as one of its own {constant} references and failed on. Moved that logic into its
   own ExtractLibVlc.ps1 file instead, called with plain parameters - sidesteps the
   collision entirely rather than just escaping around it.

v0.54b
 - Fixed the Inno Download Plugin link in MediaTagger.iss, which 404'd - it turned out to
   be a stale, unofficially-mirrored 2015 project with no reliable current home. Rewrote
   the download logic to use Inno Setup's own built-in support instead (added in 6.1),
   which removes that dependency entirely - one fewer thing to install, one fewer broken
   link.

v0.54a
 - Added MediaTagger.iss, an Inno Setup script as an alternative to the PowerShell
   installer: a traditional wizard-style Setup.exe with a Start Menu entry and a proper
   uninstaller, using the same detect-and-fetch approach for the .NET Runtime and VLC.
   Needs Inno Setup and the Inno Download Plugin to compile - both free, neither bundled.

v0.54
 - New installer option: MAKE-INSTALLER-PACKAGE.bat builds a small download that fetches
   the .NET Runtime and VLC itself on first run, instead of bundling both into one large
   exe. Install.bat is what people run - checks for an existing VLC install first and only
   downloads LibVLC if there genuinely isn't one. MAKE-EXE.bat's large all-in-one build is
   unchanged and still there if preferred.
 - If VLC genuinely can't be found anywhere, Media Tagger now shows a clear explanation
   instead of an unhandled crash.

v0.53
 - New "Excluded from scanning" section in Settings, under Duplicate Finder: two separate
   lists for excluded folders and excluded files, each with Add/Remove buttons.

v0.52
 - "Boolean Tags..." button moved above its description.
 - File size range boxes are now a fixed width, with the KB/MB/GB dropdown sitting directly
   against them instead of pushed to the right of the panel.
 - Added a "Help" link in the Boolean Tags popup with a quick guide and examples.

v0.51
 - Reordered Advanced Filtering: Exclude tags now comes before Boolean Tags.
 - The "Boolean Tags..." button no longer has a heading above it.
 - AND / OR / NOT / ( / ) buttons added above the boolean expression box.
 - Typos in a boolean expression's tag names are now flagged as an error, listing every
   unknown tag if there's more than one.
 - "Clear tag filters" renamed to "Clear All Tag Filters." and now also clears the boolean
   filter.
 - The file size filter boxes no longer stretch to fill the panel; capped to 16 digits.
 - Settings > About now shows both Duplicate Finder version numbers, matching its own title bar.

v0.50
 - Exclude tags moved under its own collapse arrow in Advanced Filtering.
 - Added a "Boolean Tags..." popup in Advanced Filtering: write an AND / OR / NOT tag
   expression with parentheses, with clickable chips for each tag.
 - Split the changelog into separate Media Tagger and Duplicate Finder lists, each with its
   own version number, viewable from Settings > About.

v0.47
 - Thumbnails are now cached on disk, so reopening a folder loads them instantly instead of
   regenerating every one. An edited file still gets a fresh thumbnail automatically.
 - New "Thumbnail cache" section in Settings, matching the Duplicate Finder's fingerprint cache:
   keep between sessions (on by default), clear on exit, a configurable cache folder, and Open
   folder / Clear cache buttons.

v0.45
 - Advanced Filtering moved up to sit directly below Filter by tags, rather than under
   Filter by type.
 - Fixed tags being greyed out in Any mode. Availability was worked out from the results as they
   already stood, so a tag matching nothing right now looked unavailable - when ticking it in Any
   mode would have added files. Counts in Any mode are now worked out against everything the
   other filters left, not the current tag match.
 - Greying out now means only one thing: a tag is greyed in the include list when it has been
   excluded, and greyed in the exclude list when it has been included. The two contradict, so
   neither is left looking active.
 - Typing a tag in full no longer removes it from the suggestion list. With tags "Dan" and
   "Daniel", typing "Dan" kept only "Daniel", which read as "Dan" not existing. Pressing Enter
   on an exact match submits rather than completing it to itself.
 - Version numbering note: the two builds before this one were labelled v0.43 and v0.43a, but
   both actually reported v0.44 internally - the version bump had silently failed. Their changes
   are the ones listed above and are all present here. Nothing was lost; only the labels were
   wrong.

v0.44
 - "Filter by tags" is back to exactly how it was before v0.43 - just the tag checklist and Clear
   button, nothing added to it.
 - The All/Any matching and tag exclusions moved into a new collapsible "Advanced Filtering"
   section below Filter by type, with its own "Reset advanced filters" button. Everything in it
   is off by default, and the header shows a short summary when anything is set, so filters left
   on can't silently hide files while the section is collapsed.
 - Fixed the arrow keys and Enter jumping to the thumbnails while picking a tag from the
   autocomplete list. Arrowing into that list moves focus off the text box, which the shortcut
   handler read as "not typing any more" and took the keys. It now treats an open suggestion list
   as still typing. Escape closes the list back to the text box, and arrowing up past the first
   suggestion returns to typing rather than sticking.

v0.43
 - Tag filtering can now do OR and NOT, not just AND.
   - A "Match: All / Any" toggle above the tag list. All is the default and behaves exactly as
     before, so nothing changes unless it's switched. Any shows files carrying at least one of
     the ticked tags.
   - A collapsible "Exclude tags" list underneath, with its own checklist. Anything ticked there
     hides files carrying that tag, whatever the filter above matched - so "beach or mountains,
     but never blurry" is now expressible.
   - Excluding "No tags" is the way to say "only files I have actually tagged".
 - The exclude list header shows how many tags are currently excluded, so it is obvious files are
   being hidden even when the section is collapsed.
 - "Clear tag filters" now also clears the exclusions and returns the match mode to All, rather
   than leaving files hidden with nothing on screen explaining why.

v0.42
 - Scrolling in the viewer when opened from a duplicate group moves only between the files in
   that group, so duplicates can be compared against each other directly rather than wandering
   off through the wider library.

v0.41
 - Bookmark names are left-aligned rather than stretched across the panel.
 - Hovering the bin icon highlights the whole row, so it's clear which bookmark is about to go.
 - Ctrl+click picks several bookmarks at once and loads them together as one library; selected
   rows stay highlighted and the panel stays open so more can be added. A plain click still
   loads just that one and clears the selection.
 - Removing a bookmark while several are selected offers to remove all of them, listing each one
   in the confirmation. Only the bookmarks go; the folders are untouched.
 - Fixed bookmarks not reappearing after a restart. They were being saved but never read back in
   on startup, so the list always came up empty.

v0.40a
 - Select Folder(s) dropdown: the panel's left edge now lines up with the button rather than the
   path box. "Browse Folder(s)..." and "+ Add Folder Bookmark" are left-aligned and no longer
   stretch the full width - they're sized to fit their text, both matching the wider of the two.

v0.40
 - Viewer: the Loop label is now white and bolder so it reads against the dark bar, and both full
   screen buttons say "Full Screen" instead of using a symbol that rendered too faintly to see.
 - The folder path box is now editable - type or paste a path and press Enter to load it, or
   press Escape to abandon the edit. A path that doesn't exist says so rather than silently
   doing nothing.
 - "Select Folder(s)" is now a dropdown panel, spanning the width of the folder path box:
   - "Browse Folder(s)..." at the top does what the old button did.
   - A "Bookmarked Folders" list beneath it, each entry clickable to jump straight to it.
   - "Add Folder Bookmark" sits where the next bookmark will appear.
   - A bin button on the right of each bookmark removes it, after confirmation. Only the
     bookmark is removed; the folder itself is untouched.
 - Bookmarks are saved between sessions. A bookmark whose folder has since been deleted or moved
   reports that rather than failing quietly.

v0.39
 - Viewer: the thumbnail pane now follows along, keeping its highlight on whatever the viewer is
   showing. Closing the viewer leaves the grid on the last file viewed rather than the one
   originally opened.
 - Viewer: added a Loop checkbox and a full screen button to the playback bar.
 - Viewer: the mouse wheel moves to the next/previous file.
 - Folder Options: "Display Sub-folder Contents" and "Show Folders in Thumbnail Pane" can be used
   together again - ticking one no longer unticks the other. Browsing into a folder with
   sub-folder contents on now pulls in everything beneath wherever you land.
 - Fixed turning off "Show Folders in Thumbnail Pane" reverting to an earlier folder. It now
   keeps whatever folder you had browsed to, which becomes the selected folder.

v0.38
 - Fixed the viewer being stuck once a video finished: pressing play did nothing and the slider
   couldn't be moved. VLC parks a finished video in a state where both are ignored until it's
   stopped and started again. Play now restarts from the beginning, and dragging the slider
   restarts and jumps to wherever you dropped it.

v0.37d
 - Fixed the viewer's controls still not returning on mouse movement (clicks and keys already
   worked). The check for whether the viewer was the active window was returning false, because
   the video surface holds focus itself - so movement was discarded, and a click only got
   through because clicking re-activated the window first. It now checks whether the pointer is
   over the viewer instead, which doesn't depend on focus.

v0.37c
 - Fixed the viewer popup starting silent while the button showed sound was on. Volume was being
   set before playback had actually begun, which VLC ignores. It is now re-applied the moment
   playback starts, so the button and the actual sound agree.
 - Fixed the viewer's controls disappearing and not coming back on mouse movement or clicks. The
   video plays on a native surface that takes mouse input before the window sees it, so once the
   pointer was over the video the window stopped noticing it moving at all. Movement, clicks and
   scrolling are now detected on the overlay layer instead, which does receive them.
 - Keyboard shortcuts in the viewer are also more reliable for the same reason: they no longer
   depend on which part of the window happens to hold focus.

v0.37b
 - Fixed the viewer popup's playback controls being invisible. You were right that the video was
   covering them: the video plays on a native surface that WPF cannot draw over, so the controls
   were rendering behind it. They now sit inside the video element itself, which is the one place
   they will draw on top.

v0.37a
 - Fixed a build error in v0.37: double-click on a thumbnail was wired using an event that only
   exists on Controls, and the thumbnail tile is a Border. Now detected from the click count on
   the ordinary mouse-down event instead, which works on any element.

v0.37
 - Fixed audio never playing in the preview pane. Only video was routed to the player; audio
   files fell through to the still-image path and just showed a static thumbnail. Audio now
   plays, with the same transport controls as video.
 - Tag suggestions now include tags that contain the typed text anywhere, not only ones starting
   with it. Prefix matches are still listed first, so typing "cat" offers "cat-photos" before
   "bobcat".
 - Right-click: "Open" is now "Open with System Default", and there's a new "Open in Pop-Up
   Window" alongside it.
 - New viewer popup for watching video, playing audio and viewing images without leaving Media
   Tagger. Left/Right arrow keys (or the arrows down either edge) move through the files, Space
   plays/pauses, F11 toggles full screen, Esc leaves full screen or closes. The controls and
   filename bar fade out after a couple of seconds without mouse movement and return on the next
   move. No taskbar button.
 - Double-click or Enter on a selected file opens it. Whether that uses the viewer or the system
   default app is a new setting under Settings > Media Tagger > Files; the viewer is the default.
   The right-click menu always offers both regardless.

v0.36
 - Folder Options: the two folder modes are now properly mutually exclusive. Previously only
   Show Folders cleared Include Subfolders and never the other way round, so Show Folders stayed
   clickable while subfolders were on. Ticking either now unticks the other, and clicking the
   ticked one turns it off - neither is greyed out, so you can switch modes with a single click.
 - "Include subfolders" renamed "Display Sub-folder Contents".
 - Arrow keys move through the thumbnails: Left/Right one at a time, Up/Down by a full row.
   Hold Shift to extend the selection, matching shift-click. The grid scrolls to follow.
 - File size added to the Selected File line, between the media type and the modified date.
 - New setting: show hidden files and folders (Settings > Media Tagger > Files), off by default
   to match File Explorer. Also hides folders inside hidden folders, which Windows doesn't mark
   as hidden themselves. Applies immediately.

v0.35
 - Fixed "Show Folders in Thumbnail Pane" leaving Include Subfolders ticked. It was greyed out
   but still showing a tick, which read as "subfolders are still being included, just locked" -
   the opposite of what folder browsing actually does. It now unticks.
 - The refresh button is now wider, labelled "Refresh", and uses the same blue as the Duplicate
   Finder's Scan button.

v0.34
 - New folder icon: a proper yellow file-folder with a few file cards fanned out and peeking
   from the opening, replacing the plain emoji glyph.
 - Folder Options: removed the Apply button - every option now takes effect as soon as it's
   changed. Also removed the "Show Folders..." description text.
 - Folder Options: options that don't currently apply are greyed out rather than hidden, so the
   panel keeps its shape and it stays visible that they exist.
 - "Add exclusion..." now opens in the folder you're currently browsing, rather than always
   jumping back to the top-level selection.
 - The up button is now labelled "Parent Folder" rather than being an unlabelled arrow.
 - The folder row is capped at two rows tall and scrolls beyond that, so a folder with dozens of
   subfolders can't push the file grid off the bottom of the window.

v0.33
 - Replaced the previous "folders shown in thumbnail pane" checklist - which turned out not to
   be what was wanted - with an actual folder browser. Folder Options now has "Show Folders in
   Thumbnail Pane": when on, folder icons appear at the top of the file grid for every subfolder
   of wherever you're currently looking, and clicking one navigates into it, one level at a
   time. An up-arrow next to the current folder's name goes back, same as a plain file browser.
 - While Show Folders in Thumbnail Pane is on, Include Subfolders is hidden - browsing one level
   at a time and recursively flattening everything behind the scenes don't mix, so the panel
   only shows whichever of the two currently applies.
 - With several folders selected, folder browsing follows just the first of them; there's no
   single parent folder that would make "up" mean anything across multiple unrelated trees. Not
   persisted across restarts, matching the other controls in this same panel.

v0.32
 - Tag Library: added "Select All" to the top of both the Add and Remove lists.
 - Tag Library: shift-click now range-selects, same as the file grid - click one tag, shift-click
   another, and everything between them is set to match.
 - Replaced the "Include subfolders" checkbox with a "Folder Options" dropdown, which now also
   has: a checklist of the currently selected folders so any of them can be temporarily hidden
   from the thumbnail pane without re-opening the folder picker; and, when subfolders are
   included, the ability to exclude specific subfolders from the recursive scan by browsing to
   them. Changes in this panel apply when "Apply" is clicked, rather than immediately on each
   checkbox - several of these are usually adjusted together, and rescanning after every single
   click would be wasteful. Folder visibility and subfolder exclusions reset whenever a genuinely
   new folder selection is made, the same way the type filter already does; they are not
   persisted across restarts.

v0.31a
 - Fixed a crash on every launch, introduced in v0.31. The new "Current Folder(s)" checkbox
   starts ticked, and WPF fires its Checked event the moment that's set while the window is
   still being built - before the checkbox below it exists yet - so reading it crashed
   immediately. It now waits until the window has actually finished loading, the same way every
   other checkbox in the app already does.

v0.31
 - Added a Tag Library: a list of tags kept independently of any folder, so autocomplete can
   suggest tags from your whole collection, not just ones already used in what's currently
   loaded.
 - Two new checkboxes at the bottom of Tags, "Autocomplete Tags From: Current Folder(s) / Tag
   Library", control which of the two feed the suggestion popup. Both are on by default.
 - New "Tag Library..." button opens a window with three pages: Current List (a plain read-only
   view of what's in the library), Add Tags (every tag used in the current folder, with a
   checkbox for each one not already in the library - those already there are greyed out - plus
   a text box for typing in tags that aren't necessarily used anywhere yet), and Remove Tags
   (the full library, each with a checkbox, annotated if it's also used in the current folder).
 - Add and Remove both list exactly what they're about to change and ask for confirmation before
   applying anything. Closing the window with something ticked or typed but not yet applied
   asks whether to discard it first.
 - Removing a tag from the library only affects future autocomplete suggestions - it never
   touches any file or the tags already on it.

v0.30a
 - Actually fixed the preview pane running off-screen this time. Two problems, both in the
   previous fix: the clamp only ran on MainContentGrid_SizeChanged, which fires when the window
   itself is resized - but dragging a GridSplitter redistributes column widths without changing
   the grid's own size, so it never fired for the actual drag that causes the problem. Both
   splitters now clamp on DragCompleted instead, which is the event that actually corresponds to
   a resize happening. Second, the clamp itself read ActualWidth immediately after a splitter had
   just changed a column's Width in the same call - but ActualWidth only updates on the next
   layout pass, so it could still read the pre-drag value and let an oversized column through
   uncorrected. It now reads the Width value the splitter just set instead, which updates
   synchronously.

v0.30
 - Fixed the preview pane running off the right-hand edge of the window and appearing cut in
   half. The thumbnail pane's minimum width was being honoured even when that made the columns
   total wider than the window, and the overflow simply ran off-screen rather than being
   refused. The preview column's width is now capped to what actually fits, both while dragging
   the splitter and when the window is resized afterwards.
 - "(native ###x###)" now expands the preview pane itself - widening the pane as well as
   raising its height - rather than only growing the area inside the existing pane width. It
   still stops at the same limits that apply to dragging the splitter by hand, so it can never
   push the thumbnail grid below its minimum or run past the window.
 - The preview's maximum height is now derived from the window instead of a fixed 500px, so
   expanding to native on a tall screen is no longer needlessly capped.
 - Removed the video preview's black bars, which tore and smeared while resizing. The VLC
   surface was being handed a box that did not match the video's shape, so it drew its own
   letterboxing - and that hosted surface repaints out of step with the rest of the layout.
   It is now given a box matching the video's aspect ratio exactly and centred, so there are
   no bars to draw in the first place.
 - Pressing Enter in the Add Tags box now saves the typed tag(s), same as clicking the button.
   If a suggestion is showing, Enter completes it first (matching Tab) rather than submitting
   straight away and cutting off what was being typed - Enter again once nothing is left to
   suggest is what submits.

v0.29
 - Fixed the file-type filter (Images/Videos/Audio extension checkboxes) not resetting when a
   genuinely new folder was selected. It was designed to survive being rebuilt so tagging a file
   didn't silently reset the view - but that same protection was carrying a filter set up for one
   folder over into an unrelated one. Refresh (reloading the same folders) still keeps it; picking
   a new folder through Select Folder(s) now starts clean.
 - File Size filter's Min box now defaults to KB rather than MB.
 - The "(native ###x###)" text under the preview is now clickable: it expands the preview toward
   the media's actual pixel size, as far as the pane's existing 120-500 height limit allows.
   Click again to go back to the normal auto-sized preview. Stays expanded across different file
   selections until turned off.
 - Settings: fixed the video preview description, which still said the three checkboxes mirrored
   the live preview controls - they haven't since they became startup-only defaults.
 - Settings: added "Limit preview size to native resolution" under Video preview, off by default.
   Unlike autoplay/loop/mute, this one applies live: it stops a smaller image or video being
   stretched past its real pixel size and left looking soft.
 - Settings: "On startup" renamed "Startup Folder", with "Reopen the folders I had open last
   time" moved to the top. The separate "Default folder" heading is gone - the folder path box
   now sits directly under "Open default folder", the radio option it belongs to.

v0.28
 - Added "File Size" as a new category under Filter by type, alongside Images/Videos/Audio.
   Off by default; ticking it reveals a Min and Max box, each with its own KB/MB/GB unit, and
   hides files outside that range. Leaving either box empty means no limit on that side, so a
   Max alone works as "under this size" and a Min alone as "over this size."
 - The Filter by type counters now account for the size range when it's turned on, same as they
   already did for the extension checkboxes.
 - Fixed the preview pane being able to cover the entire file grid. The centre column had no
   minimum width, so dragging the right splitter far enough left could squeeze the thumbnails
   away to nothing. It now has a floor of one tile column plus the scrollbar, and tracks the
   thumbnail size chosen in Settings rather than a fixed number, so the limit stays correct at
   any tile size.

v0.27a
 - Fixed a crash on Settings Cancel (and closing Settings with the X). Cancel reverts the whole
   settings object in one shot regardless of what was actually touched, which was unconditionally
   rebuilding the file grid's panel every single time - including when virtualization was never
   changed. A layout pass already queued for the panel being replaced could then fire after the
   swap and hit invalid internal state, crashing with a NullReferenceException that repeated on
   every subsequent layout pass, stacking one error dialog after another.
 - The panel rebuild now only happens when the virtualization setting actually changed. The
   panel itself is also now defensive about being measured after a swap, so the same class of
   bug fails quietly instead of crashing if it is ever triggered a different way.

v0.27
 - "Reopen the folders I had open last time" now also restores whether "Include subfolders" was
   ticked and which file types were filtered out - previously only the folder list itself carried
   over, so every restart reset back to showing everything. These only apply on that startup
   mode; opening the default folder always starts from the plain default, since that folder may
   have nothing to do with what these described last.
 - Settings: dropped the "default is 110 px" and "no thumbnail cache" notes, both minor
   asides that clogged the page more than they helped.
 - Settings: the video preview checkboxes (autoplay/loop/start muted) now set what a *future*
   launch starts with, instead of also changing the current session the moment they're ticked.
   Adjusting playback right now is still done from the checkboxes under the preview panel; the
   two were easy to confuse when one dialog did both.
 - Settings: "Close" is now "Done" and "Cancel", not one button doing both jobs. Cancel restores
   every setting to how it was when the window opened, including live ones like thumbnail size
   and virtualization that had already been previewed - closing the window with the X does the
   same, on the reasoning that dismissing without an explicit "keep this" most likely wasn't
   meant to keep anything. Done saves.
 - Settings now opens with the Media Tagger section expanded when opened from Media Tagger's
   gear, or Duplicate Finder's when opened from there.

v0.26
 - Settings is now split into collapsible "Media Tagger" and "Duplicate Finder" sections, each
   with its own headings, instead of one long list mixing the two.
 - "Thumbnail height" is now "Thumbnail Size" and scales the tile's width and height together,
   so tiles keep their shape instead of only growing taller. The readout shows both dimensions.
 - MT: clicking empty space in the file grid clears the selection, as Explorer does.
 - MT: a count of selected files now sits beside "Selected File", so a multi-selection is visible
   without counting highlighted tiles - which matters because Add and Remove Tags act on all of
   them, not just the one being previewed.
 - MT has no thumbnail cache to keep or clear, unlike DF's fingerprints: thumbnails come from
   Windows and are held only while the app runs. Settings now says so rather than offering an
   option that would do nothing.

v0.25
 - Thumbnails are now generated for what's on screen first. Previously they were built in
   storage-enumeration order, which ignored both the current sort and the current filter: the
   rows you were looking at stayed blank while images appeared at no obvious position, and a
   folder of thousands generated every thumbnail even when the filter showed forty.
 - Thumbnail work now follows the filtered, sorted list, and re-prioritises as you scroll or
   change the sort. Work already done is kept, since a thumbnail stays valid wherever the file
   moves to in the list.
 - Thumbnails are generated a few at a time rather than strictly one after another. Extraction
   waits on the disk, so a small amount of parallelism helps; it is deliberately capped, since
   too many parallel reads on an external drive turn into seek thrash and get slower.

v0.24a
 - Fixed the file grid only showing about three rows. The ScrollViewer was wrapping the file
   list from outside, which meant the new virtualizing panel was measured against an unbounded
   height and never learned how big the visible area actually was, so it built almost no rows.
   The ScrollViewer now sits inside the list's own template, where the panel can see it.
 - Fixed tag filters clearing themselves whenever a tag was added or removed. Refreshing the
   tag counts rebuilt the checklist from scratch and dropped which boxes were ticked, so every
   tag edit silently reset the view to showing everything. Ticked tags and unticked file types
   now survive the rebuild.

v0.24
 - Undo (Ctrl+Z) for tag changes. A bulk tag across hundreds of files is one undo step, not
   hundreds. Session-only and rename-only by design: a stored history would go stale the moment
   files moved outside the app, and move/copy/delete leave the original location so pretending
   to reverse them would be a promise this can't keep.
 - The file grid now only builds the tiles on screen. Previously every file in the folder got a
   tile and a decoded thumbnail up front and held it, so a few thousand files was slow to open
   and could run out of memory. Can be switched off under Settings > Performance.
 - Tags are now checked for characters that can't go in a filename. Typing "holiday:2024" used
   to throw part-way through a rename; it now says which characters are the problem before
   touching anything.
 - Long filenames are checked before renaming. Tags lengthen names, and exceeding the Windows
   260-character limit used to fail mid-batch leaving some files renamed and some not.
 - Name clashes are now reported. A tag change that had to append "(2)" to avoid overwriting
   an existing file used to do so silently.
 - Media Tagger now ignores macOS "._" sidecar files, as the Duplicate Finder already did. They
   were appearing as broken tiles and could be tagged by accident.
 - Search box above the filter pane. Matches anywhere in the filename, tags included; multiple
   words must all match but in any order.
 - Keyboard shortcuts: Ctrl+F search, Ctrl+A select all, Ctrl+Z undo, Delete, F2 tag box,
   F5 refresh, Escape to clear search then selection. Suppressed while typing in a text box.

v0.23
 - Settings are now saved. They persist to settings.json under your AppData folder and are
   written when the app or the Settings window closes. Previously everything reset on restart.
 - Settings: the thumbnail slider now states the default size and has a "Reset to default" button.
 - Settings: new "On startup" section - open a default folder, or reopen whatever folders were
   loaded last time. Opening the default folder is selected by default, and the default folder
   itself can be browsed for or typed in. A folder that no longer exists is skipped quietly.
 - Removed the Changelog link from the footer; it now lives in Settings under About. The footer
   still shows the version.
 - Fixed changelog word wrapping. The source text is hard-wrapped for readability, and the
   display was wrapping it a second time at the window edge, so entries broke twice and left a
   ragged left margin with stray one-word lines. Each entry is now rejoined into one logical
   line before display, leaving all the wrapping to the window, which also got wider.

v0.22
 - The "### of ### files shown" count moved out of the toolbar to sit above "Selected File",
   reading "### of ### Total Files Shown." The toolbar status line is now free for transient
   messages without the two overwriting each other.
 - Added a settings gear to the top right. Thumbnail size, video preview defaults (autoplay /
   loop / start muted) and the Duplicate Finder's fingerprint cache (size, open folder, clear)
   are all wired to live behaviour. Settings apply to the current session - there is no config
   file yet, and toggles that silently reset on restart would be worse than being upfront.

v0.21
 - Decoders are now picked by file extension rather than tried in turn. Previously every file was
   pushed through Media Foundation first; with images in the mix that would have meant thousands
   of pointless decode attempts and a log full of misleading failures.
 - If some listed folders no longer exist (an unplugged drive, say) the scan offers to continue
   with the rest rather than refusing outright - but says which are being skipped.

v0.20
 - Merged in the Video Duplicate Finder (previously a separate app, at its v0.05). It lives
   under a new "Duplicate Tools" section in the right-hand panel and opens in its own window,
   so you can keep tagging while a scan runs. Clicking the button again brings the existing
   window forward rather than opening a second copy.
 - It finds videos with matching content even when resolution, compression, cropping or length
   differ, and scans whatever folder you pick inside it - independently of the files loaded in
   Media Tagger.
 - Adopted the duplicate finder's visual style across both windows (shared palette, buttons,
   cards and headings) so the two read as one application.
 - Build changes for the merge: the project now targets net9.0-windows10.0.19041.0 rather than
   plain net9.0-windows, because the frame extractor uses WinRT APIs that are only available
   when a Windows SDK version is pinned. Microsoft.Data.Sqlite moved 8.0.8 -> 9.0.0 (still MIT).
 - Added RUN.bat, CLEAN-REBUILD.bat and MAKE-EXE.bat for easier testing between versions.

v0.19
 - All collapsible section headings (Filter by tags, Filter by type, Preview, Tags, Organise Into
   Folders) are now bold
 - Added a refresh button (⟳) next to "Select Folder(s)..." to reload the current folder(s) from
   disk without reopening the picker
 - "Add tags" relabeled "Add Tags", "Remove tags" relabeled "Remove Tags"
 - Remove Tags is now a plain checklist instead of tri-state - every listed tag starts unchecked,
   and you just check the ones you want gone (help text updated to match)
 - Each Remove Tags row's "(## of ##)" count is now styled the same light grey as the other
   counters in the app, next to the checkbox rather than baked into its label

v0.18
 - Reworked tag editing under "Tags": the box is renamed "Add Tags" and is now purely additive
   for both single and multiple files - no more ambiguity between "replace this file's tags" and
   "add to every selected file" depending on how many files happen to be selected
 - Added "Remove Tags": a tri-state checklist of every tag present on the current selection
   (checked = on every selected file, greyed = on some), with a "(## of total selected)" count
   per tag. Check the ones you want gone and click Remove Tags - the first way to bulk-remove
   tags from multiple files at once
 - The tag box no longer pre-fills with the selected file's current tags (that's what the Remove
   Tags list is for now) and the old "will rename to..." live preview was dropped along with it

v0.17
 - "Filter by type" counters now always show shown-vs-total ignoring the tag filter entirely
   (previously toggling tags made the type counters jump around, which was confusing since tags
   and types are separate filters)
 - Added a matching counter to "Filter by tags": "(### of ###)" in its header, showing how many
   of the files that already passed the type filter are still visible once tags are applied too

v0.16a
 - Fixed: selecting a new video while "Preview" was collapsed would still start autoplaying it in
   the background. It now stays paused until you expand Preview (if Autoplay is on, it plays as
   soon as you expand)

v0.16
 - Collapsing "Preview" now pauses a playing video; expanding it again resumes playback only if
   Autoplay is checked
 - Selecting multiple files (ctrl/shift-click) now shows file info and preview for the last file
   you clicked, instead of always the first one in the list
 - The "Filter by type" (shown of total) counter now sits in the section's header, so it stays
   visible even when the section is collapsed

v0.15
 - Reworked the right panel's layout: "Selected File" is now a plain header (no collapse arrow)
   showing filename, kind/modified date, and the containing folder (small grey text) up top;
   "Preview" is now its own collapsible section with just the image/video and playback controls;
   "Tags" is a separate collapsible section with the tag box and Save Tags button
 - "Organize into folders" renamed to "Organise Into Folders"
 - Save Tags now works with multiple files selected: it adds the typed tag(s) to every selected
   file (existing tags kept, duplicates ignored - same merge as the right-click "Add/Change
   Tags..." dialog), after a confirmation prompt so a bulk change is never accidental

v0.14
 - Added a collapse/expand arrow to "Organize into folders" and to "Selected file" - the latter
   collapses the preview image/video and every control under it (tags box, Save Tags, playback
   controls) in one go, so you can shrink the right panel down to just the file list when you're
   not actively tagging something

v0.13
 - Added "Open containing folder" to the right-click menu
 - Filter by type now shows shown/total counts throughout: "(50 of 100)" under the "Filter by
   type" heading for the overall total, and per-category too, e.g. "Images (50 of 100)" means 50
   images are currently visible out of 100 images in the loaded library
 - An Images/Videos/Audio category checkbox now reflects its extensions' state: greyed out
   (indeterminate) if some but not all extensions are checked, normally checked if all are, and
   unchecked if none are
 - Clicking a category checkbox now ticks/unticks every extension checkbox underneath it in one go

v0.12
 - "Select Folder(s)..." now supports real multi-select right in the same native folder-picker
   screen - check off several folders in one visit, no follow-up "select another?" prompt.
   (This required bumping the project to .NET 9 - see README if `dotnet run` complains about the
   SDK version.)
 - Tag autocomplete (matching existing tags as you type) now also works in the right-click
   "Add/Change Tags..." dialog, not just the preview pane's tag box
 - Added "Properties..." to the right-click menu - shows the real Windows Properties dialog for
   a single file, or a combined size/count summary when multiple files are selected
 - "Sort by" has a new "Size" option

v0.10
 - Filter by type: "Images"/"Videos" now show a live count, and there's a new "Audio" category
   (.mp3, .wav, .flac, .aac, .ogg, .wma, .m4a) with its own expandable extension list
 - Filter by type now sits directly under Filter by tags in one flowing panel, instead of being
   pinned to the bottom - it shifts up/down naturally as either section's expanders open or close
 - Renamed "Browse Folder..." to "Select Folder(s)..." and it now supports selecting multiple
   folders at once (their files are merged into one list, de-duplicated)
 - "Sort by" has a new "Random" option
 - Fixed "Preview scale" to show zoom relative to the file's actual native resolution (e.g.
   "42% (native 4032×3024)"), instead of a meaningless ratio against the pane's own width
 - Fixed the video scrub bar only sometimes jumping to the clicked point - it now computes the
   click position directly instead of relying on WPF's flaky built-in behavior, so a plain click
   always seeks correctly

v0.09
 - Fixed mute button not reliably muting/unmuting video audio
 - Mute button now shows a muted-speaker icon (🔇) while muted
 - Volume slider is greyed out (disabled) while muted

v0.08
 - Shift+click now does range selection like a file manager (Ctrl+click still toggles
   individual files)
 - Right-click menu: "Add/Change Tags..." lets you add a tag to every selected file at once,
   keeping each file's existing tags
 - Volume slider and mute button added to video preview
 - Loop checkbox added next to Autoplay (on by default) — video restarts instead of stopping
 - Fixed: clicking directly on the video scrub bar now jumps to that point (previously only
   dragging worked)
 - "Organize into folders" moved from the left filter panel to the right preview panel

v0.07
 - Added a "No tags" filter entry, always pinned to the bottom of the tag list, for finding
   files that have no tags at all
 - "Filter by type" moved to below "Filter by tags"
 - Filter by type is now an expandable tree: Images/Videos expand to reveal per-format
   checkboxes (.jpg, .png, .mp4, .webm, etc.)

v0.06
 - Autoplay is now on by default for video preview
 - Preview pane can be resized much wider (until the center column is squeezed away)
 - Preview image/video now scales in both directions as you resize the pane, with a
   live "Preview scale: NN%" readout (100% = the pane's original default width)

v0.05
 - Adjustable pane widths (drag the dividers between panels)
 - Tag autocomplete: suggestions appear as you type new tags
 - Video preview scrub bar (slider)
 - "Open" added to the right-click menu
 - Filter by file type (Images / Videos), shown above tag filters
 - Filter sections can be collapsed/expanded via the arrow next to their titles
 - Version number and changelog link added (bottom right)

v0.04
 - Video playback rebuilt on LibVLC for reliable mp4/webm/mkv/etc. support
 - Multi-select with visual highlighting (Ctrl+click)
 - Right-click menu: Move to folder, Copy to folder, Delete (Recycle Bin, with confirmation)

v0.03
 - Organize simplified to always group by selected tag(s)
 - Tag filter list reorders so available tags float to the top
 - Video preview added (Play/Pause, Autoplay checkbox)

v0.02
 - Fixed silent startup crashes; errors now show a message box
 - Fixed a timing bug where UI events could fire before the window finished loading

v0.01
 - Initial release: browse a folder, tag files via filename, sort, filter by tag, and
   organize into subfolders
""";

        private const string DuplicateFinderChangelogText = """
Duplicate Finder — Changelog

v0.19
 - "What happened?" now suggests installing ffmpeg when a scan hit files it could not read,
   with a link to the official download page. Only shown when there were actually failures
   and ffmpeg isn't already installed. ffmpeg is picked up automatically on the next scan
   once present - nothing to configure.
 - The installer is now named "TagCat-Setup v0.<TagCat>.<DF>.exe" and no longer says
   MediaTagger.

v0.18
 - Renamed to TagCat Duplicate Finder, following the rename of the main app.
 - The fingerprint cache moved to %LocalAppData%\TagCat. It is not carried over, so the
   next scan rebuilds it and will be slower than usual, once.

v0.17
 - Added folder and file exclusions for scans. Reviewed and edited in Settings. Each scan
   mode's advanced options has its own checkbox to include excluded items anyway, just for
   that scan.
 - "Folder" button replaced with a "More Options" dropdown: Open Containing Folder, Open
   Folder in Media Tagger (selects the file once it loads), Open all Folders in Group,
   Exclude Folder for This Session, Exclude Folder for All Sessions, Exclude File for All
   Future Sessions, and Copy File Path. The two permanent exclude options confirm first and
   update the results already on screen immediately, without re-running the scan.

v0.16
 - For an Express (exact-copy) match, the suggested keeper is now chosen by filename instead
   of an arbitrary tie: a clean name beats "- Copy", which beats "name_2", which beats
   "name (2)". Oldest file wins if names don't decide it.
 - Preview thumbnails are now in colour instead of black and white.
 - Play and Open Folder buttons moved above the file info instead of beside it.
 - "Folder" button renamed to "Open Folder", and now matches Play's width.

v0.15
 - File size range boxes are now a fixed width, with the KB/MB/GB dropdown sitting directly
   against them instead of pushed to the right of the panel.
 - Removed the reference to Media Tagger's own size filter in the file size description.

v0.14a
 - Recovered and added the standalone app's own changelog from before it was merged in
   (v0.01 through v0.05), at the bottom of this list.

v0.14
 - "What happened?" now shows after every scan, not just when something looks wrong.
 - The file size filter boxes no longer stretch to fill the panel; capped to 16 digits.

Version numbers before the Duplicate Finder had its own independent numbering match
whichever Media Tagger release it shipped alongside at the time.

v0.49
 - Express now finds duplicates by checksumming actual file content instead
   of comparing names/dates - a match now means the files are provably byte-for-byte identical,
   not just similarly named. Files are only read at all if another file already shares its
   exact size, which is most of why it stays fast.
 - "Match File Name", "Match File Created Date/Time" and "Match File Modified
   Date/Time" are now optional additional restrictions on top of the checksum match, all off by
   default - the checksum alone is already a complete answer, these only ever narrow it further.
 - Express moved to the leftmost preset card.
 - Express can now scan for audio duplicates too, since comparing raw bytes
   works on any file type. Still greyed out for the other three presets, which need an actual
   content fingerprint audio doesn't have here. Ticking it and switching presets doesn't clear
   the tick - switching back to Express restores it.
 - The audio note now says "(only available in Express scan)" instead of
   "(audio matching not implemented yet)".

v0.48a
 - Fixed a crash on opening the Duplicate Finder, introduced in v0.48. "Match File Name" starts
   ticked, and WPF fires its Checked event the moment that's set while the window is still being
   built - before a note further down the file exists yet - so checking it crashed immediately.
   It now waits until the window has actually finished loading, the same way every other
   checkbox in the app already does.

v0.48
 - Title bar now says "Media Duplicate Finder" instead of "Video Duplicate
   Finder" - it was already finding image duplicates too, so the old name undersold it.
 - New "Express" scan mode alongside Quick/Balanced/Thorough. It doesn't read
   or analyse file content at all - it groups files purely by file properties, so it's near
   instant even on a huge library. Selecting it swaps Advanced Options for its own settings:
   - "Match File Name", "Match File Created Date/Time", "Match File Modified Date/Time" - tick
     whichever you trust; files must agree on every ticked one to be grouped together.
   - A Min/Max file size range, same as Media Tagger's own size filter - only files within it
     are considered at all.
   At least one of the three match options has to be ticked, or every file would tie and land
   in one meaningless group together.
 - Results from an Express scan are labelled "Matched by file properties" rather than shown with
   a fabricated confidence percentage, since nothing was actually compared to produce one.

v0.46b
 - Fixed the duplicate list and preview pane's internal scrollbars still being
   missing after v0.46a. The Grid holding them had its MinHeight bound to the page's outer
   ScrollViewer, but MinHeight only clamps a result after layout has already been measured - it
   doesn't feed into the measurement itself, and a ScrollViewer always offers its content
   infinite height to measure against regardless. With nothing actually constraining it, the
   results area was simply growing to fit its full contents instead of scrolling. Height (not
   MinHeight) is what actually affects measurement, so that's what's bound to the ScrollViewer's
   viewport now - the results area gets a genuinely bounded height in the ordinary case, and its
   own minimum height still forces the whole page to scroll instead once Advanced Options
   squeezes it too far, exactly as before.

v0.46a
 - Reworked the "Could not read" feature from v0.46, which added a separate
   scrollable box that wasn't wanted. It's now a single clickable summary row underneath the
   duplicate groups, styled the same way a found group is - clicking it lists every unreadable
   file in the panel on the right, the same way clicking a group lists its members there.
 - Reworked the whole-window scrolling from v0.46 as well. The status bar (the
   "what happened" summary and its button) is now pinned outside the scrollable area entirely, so
   it stays visible without needing to scroll. The duplicate list and member list keep their own
   internal scrolling exactly as before. The results area now has its own minimum height (roughly
   two full member rows) - only once Advanced Options squeezes it below that floor does the whole
   page start scrolling, rather than scrolling being available all the time.

v0.46
 - Removed "Test one file" from the main screen.
 - "View log" is gone from the main screen too. "What happened?" now opens a
   proper window instead of a plain message box, with "See the full log." at the bottom as an
   actual clickable link - the only remaining way to reach the log.
 - Files that could not be read during a scan now show in a "Could not read"
   list at the bottom of the duplicate-groups list. Selecting one shows its details - why it
   failed, where it is, Play and Folder buttons - in the same panel a group's members would use.
 - The whole window scrolls if Advanced Options expands far enough to push the
   results and preview below the bottom edge, rather than squeezing them down to nothing.
 - A preset card no longer stays highlighted once its starting values have been
   hand-edited in Advanced Options - it only happens while "Use these settings instead of the
   preset" is on, since before that the sliders don't affect anything.

v0.42
 - The Duplicate Finder now plays files in Media Tagger's viewer by default, instead of handing
   them to the system's video player.
 - New setting under Settings > Duplicate Finder > Behaviour to switch back to the system default
   app if preferred. Takes effect immediately, including on an already-open Duplicate Finder.

v0.36
 - The Duplicate Finder no longer gets its own taskbar button when opened as a popup.

v0.35
 - The Duplicate Finder can open either as a separate window (the default, so you can carry on
   tagging while a scan runs) or as a popup that stays in front and blocks Media Tagger until
   closed. Choose under Settings > Duplicate Finder > Behaviour.

v0.34
 - Opening the Duplicate Finder now starts from the folder you're actually browsing, not just
   the original top-level selection.

v0.26
 - The fingerprint cache folder can now be changed, for when a large cache is better off the
   system drive. Changing it starts a fresh cache; the old file is left where it was rather than
   moved silently.
 - New "Clear fingerprints when the app closes" option, off by default. Keeps a cache's speed
   within a session while leaving nothing behind afterwards.
 - Settings gear added top right, matching MT. It opens the same settings window.
 - The fingerprint cache path is now defined in one place. It had been worked out separately in
   the finder and in the settings dialog, so changing the folder would have left one of them
   pointing at a file the other was not using.

v0.23
 - Settings: new "Keep fingerprints between scans" option, on by default. Switching it off means
   every duplicate scan examines each file from scratch and leaves nothing cached on disk. The
   Duplicate Finder's own cache checkbox greys out to match, rather than looking live but doing
   nothing.
 - Settings: new "Close Duplicate Finder when Media Tagger closes" option, on by default.

v0.22
 - MacOS "._" sidecar files are now ignored. These are metadata companions
   macOS writes beside every real file on a non-Mac-formatted drive; they carry a video
   extension but hold no media, so every decoder rejected them. On an external drive that had
   been used on a Mac they roughly doubled the file count and made a clean scan look half failed.
 - New advanced option "Include trash / recycle bin folders", off by default in
   every preset. Covers Windows $Recycle.Bin and macOS .Trashes / .Trash. Anything found inside
   one is flagged "In trash / recycle bin" in the results, since a match against something already
   deleted usually isn't the copy you care about. A folder you list by name is always scanned,
   even if it is itself a trash folder.
 - "Sort by" now defaults to File name.

v0.21
 - Scans multiple folders at once. Browse now allows multi-select, and the box
   accepts a semicolon-separated list you can edit by hand. Files reachable from more than one
   listed folder are only scanned once, so a parent and its own subfolder can both be listed
   without a file clustering against itself.
 - Duplicate Finder opens pre-filled with whatever folders Media Tagger currently has loaded, so
   scanning the library you're already working in doesn't mean picking the same folders again.
 - Duplicate Finder can now find duplicate IMAGES as well as videos, via new "Look for duplicate"
   checkboxes. A still is treated as a one-frame video, so the same perceptual hashing that
   ignores resolution, compression and cropping applies to photos too. Decoding uses Windows'
   own imaging stack, so JPEG/PNG/BMP/GIF/TIFF always work and HEIC/WebP work when the relevant
   Windows codec is installed.
 - AUDIO IS NOT IMPLEMENTED. The checkbox is present but disabled. Matching audio needs a
   spectral fingerprint, which is a different technique from the frame-based hashing used for
   video and images - it can't reuse this pipeline. See the README for what building it involves.

v0.20
 - Closing the duplicate finder now cancels any scan still running. As a standalone app closing
   it ended the process; as a child window a scan would otherwise have carried on decoding video
   in the background with nowhere to report to.

Before v0.20, this was a separate standalone app in its own right. What follows is its own
changelog up to the point it was merged in.

v0.05
 - Fixed ticked checkboxes being lost. Switching to a different group and back rebuilt the file
   list from scratch, silently resetting every tick to unticked. Tick state is now tracked by
   file path independently of the row objects, so it survives switching groups.
 - Preset cards now mirror into the advanced panel. Clicking Quick, Balanced or Thorough moves
   every slider and checkbox to that preset's real values, so the advanced panel always shows
   what a scan will actually do rather than stale numbers left over from a previous preset.
 - Visual pass: a consistent colour palette and spacing throughout, Scan established as the one
   clearly primary action, Send to Recycle Bin styled red, the preset radio buttons became
   selectable cards, Test one file / View log moved to a quieter secondary row, and group
   selection in the results list switched from the OS default blue to the app's own accent
   colour.

v0.04
Replaced the frame extraction entirely. v0.03's diagnostics showed 0 of 10 files readable,
   including plain H.264 MP4s, which ruled out codecs and pointed at the decoder instead.
 - New Media Foundation decoder, now the primary path. The previous version used the WinRT
   MediaComposition.GetThumbnailAsync API, which returns nothing for ordinary files in
   non-packaged desktop apps. Media Foundation is the decoder Windows itself uses.
 - Decoder chain with per-strategy reporting: Media Foundation, then the WinRT thumbnail API,
   then ffmpeg if installed. Whichever produces frames wins, and every failure reason is
   recorded rather than discarded.
 - Stopped swallowing decode errors. The old extractor caught per-frame exceptions and ignored
   them, which concealed the real fault for three versions. A decoder that returns no frames now
   reports why.
 - Previews use the same decoder chain, so a file that scans will also show a thumbnail instead
   of failing separately for its own reasons.
 - Partial results are discarded when a decoder fails midway, so a half-built fingerprint can
   never be compared as if it were complete.

v0.03
Diagnostics release. v0.02 ran without crashing but reported nothing, with no way to tell
   whether that meant "no duplicates" or "couldn't read a single file".
 - Added "Test one file..." - runs a single video through the whole pipeline with the cache
   bypassed and reports resolution, duration, frames requested, frames decoded and frames
   usable.
 - Added a "What happened?" button in the status bar whenever a scan finds nothing or hits
   failures, showing a per-file breakdown.
 - Scan log written to %LocalAppData%\VideoDedupe\last-scan.log, openable from a "View log"
   button.
 - The status bar now distinguishes "no duplicates among N videos examined" from "none of the N
   videos could be read", which previously looked identical.
 - Automatic ffmpeg fallback: if Windows cannot decode a file and ffmpeg is present on the
   machine, it's used instead. Nothing is bundled; it only looks on PATH and a couple of common
   install folders.
 - Failures now carry a real reason instead of a generic message, including a pointer to the
   free Web Media Extensions package for WebM and VP9 files.
 - Test runs no longer write to the fingerprint cache, so repeated tests always re-read the
   file.

v0.02
 - Fixed the crash on Scan. The "include subfolders" checkbox was being read from inside the
   background file-search task, and reading a control off the UI thread is just as illegal as
   writing to one. All control values are now captured up front and passed in as plain values.
 - Audited every remaining control access in the window; the rest were already safe.
 - The version shown in the title bar now reads correctly (was showing "v0.00.1").

v0.01
First working build.
 - Folder picker with optional subfolder scanning.
 - Quick / Balanced / Thorough presets.
 - Advanced panel: match strictness, confidence, crop tolerance, frame interval, clip detection,
   letterbox handling, cache reuse.
 - Results grouped into clusters, sortable by space, confidence, group size, duration or file
   name.
 - Review panel with previews, resolution, duration, file size, and a suggested keeper.
 - Send ticked files to the Recycle Bin, with a guard against emptying a whole group.
 - Play and Reveal-in-Folder buttons per file.
 - Fingerprint cache in %LocalAppData%\VideoDedupe\fingerprints.db so repeat scans are fast.
 - Fixed a threading crash ("the calling thread cannot access this object") caused by previews
   being built on a background thread and handed straight to the interface.
 - Error dialogs now include the exception type and the line in the app it came from.

Known gaps at v0.01: crop tolerance was partial, and a crop that reframed the subject would
still be missed. Codec support was whatever Windows provided, with unreadable files counted in
the status bar rather than silently skipped. There was no way yet to verify thresholds against
known duplicates before running on a real library.
""";
    }
}
