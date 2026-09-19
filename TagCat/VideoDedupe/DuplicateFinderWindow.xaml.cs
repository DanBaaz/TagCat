using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VideoDedupe.Extraction;
using VideoDedupe.Matching;
using VideoDedupe.Models;
using VideoDedupe.Options;
using VideoDedupe.Ui;
namespace VideoDedupe;

public partial class DuplicateFinderWindow : Window
{
    private readonly ObservableCollection<ClusterRow> _clusters = [];
    private readonly ObservableCollection<MemberRow> _members = [];
    private readonly ObservableCollection<FailureRow> _failures = [];

    /// <summary>
    /// Which files are ticked, keyed by path. The member rows themselves are
    /// rebuilt every time a group is selected, so their IsTicked would otherwise
    /// reset to false the moment you looked at a different group and came back.
    /// This is what makes a tick survive that round trip.
    /// </summary>
    private readonly HashSet<string> _tickedPaths = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private ScanResult? _lastResult;

    /// <summary>
    /// RadioButton.Checked fires the moment IsChecked="True" is parsed out of the
    /// XAML, which happens while InitializeComponent is still building the rest of
    /// the window — long before the advanced-panel sliders exist. This flag is what
    /// stops the preset handler from running before there's anything to update.
    /// </summary>
    private bool _windowReady;

    /// <summary>
    /// Where the fingerprint cache lives. Supplied by the host so the folder stays
    /// configurable in one place; falls back to the default if nothing was set.
    /// </summary>
    public string DatabasePath { get; set; } =
        Path.Combine(MediaTagger.Models.AppSettings.DefaultFingerprintFolder, "fingerprints.db");

    /// <summary>
    /// When false the fingerprint cache is neither read nor written, so every scan starts
    /// from scratch and nothing is left on disk. Set from TagCat's settings.
    /// </summary>
    public bool RetainFingerprints { get; set; } = true;

    /// <summary>Folders and files skipped by a scan unless its "include excluded" checkbox is
    /// ticked. Set from TagCat's settings, where the two lists are reviewed and edited.</summary>
    public IReadOnlyList<string> ExcludedScanFolders { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedScanFiles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Session-only exclusions from the "More Options" menu - never touches Settings, gone
    /// the moment this window closes. Kept separate from ExcludedScanFolders (the persistent
    /// list) rather than merged into it, since the two have genuinely different lifetimes.
    /// </summary>
    private readonly HashSet<string> _sessionExcludedFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Persists a folder exclusion to TagCat's settings. Left null-checked at
    /// every call site rather than assumed present, the same as every other callback here.</summary>
    public Action<string>? AddExcludedFolderPermanently { get; set; }

    public Action<string>? AddExcludedFilePermanently { get; set; }

    /// <summary>
    /// Loads a folder into TagCat and selects the file that led there. A callback
    /// rather than a direct reference, matching OpenInViewer/OpenSettings - this window
    /// needs no knowledge of what is hosting it beyond what gets handed in.
    /// </summary>
    public Func<string, string, Task>? OpenInMediaTagger { get; set; }

    /// <summary>
    /// True once TagCat's own window has closed. With no ShutdownMode set, WPF
    /// defaults to closing the whole app only once its LAST window closes - and "Close
    /// Duplicate Finder when TagCat closes" is off by default - so this window can
    /// genuinely still be open with nothing left to open a folder into. A closed WPF Window
    /// can never be shown again, so this is checked before ever offering to, rather than
    /// finding out from an exception.
    /// </summary>
    public bool IsHostWindowClosed { get; private set; }

    public void NotifyHostWindowClosed() => IsHostWindowClosed = true;

    /// <summary>
    /// Opens the host's settings window. Supplied as a callback rather than a reference to
    /// TagCat, so this window keeps no knowledge of what is hosting it.
    /// </summary>
    public Action? OpenSettings { get; set; }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();

    public DuplicateFinderWindow() : this(null) { }

    /// <param name="initialFolders">
    /// Folders to pre-fill, normally whatever TagCat currently has loaded, so
    /// opening this from a library you are already working in doesn't make you pick the
    /// same folders a second time. Null or empty falls back to the Videos folder.
    /// </param>
    public DuplicateFinderWindow(IEnumerable<string>? initialFolders)
    {
        InitializeComponent();

        Title = $"TagCat Duplicate Finder  v{DedupeVersion}  (TagCat v{HostAppVersion})";

        ClusterList.ItemsSource = _clusters;
        MemberList.ItemsSource = _members;
        FailureDetailList.ItemsSource = _failures;

        var seeded = (initialFolders ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        FolderBox.Text = seeded.Count > 0
            ? JoinFolders(seeded)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        WireSliderLabels();

        _windowReady = true;
        ApplyPresetToAdvancedPanel();
        ApplyFingerprintRetention();

        if (seeded.Count > 0)
        {
            StatusText.Text = seeded.Count == 1
                ? "Folder filled in from TagCat. Choose what to look for, then Scan."
                : $"{seeded.Count} folders filled in from TagCat. Choose what to look for, then Scan.";
        }
    }

    /// <summary>
    /// Greys out the cache checkbox when retention is off, rather than leaving a live-looking
    /// control that silently does nothing.
    /// </summary>
    public void ApplyFingerprintRetention()
    {
        if (!_windowReady) return;

        UseCache.IsEnabled = RetainFingerprints;
        CacheDisabledNote.Visibility = RetainFingerprints ? Visibility.Collapsed : Visibility.Visible;
        if (!RetainFingerprints) UseCache.IsChecked = false;
    }

    // ---- Multi-folder plumbing ------------------------------------------

    /// <summary>
    /// Semicolon is the separator because it cannot appear in a Windows path, so no
    /// escaping is needed and the box stays hand-editable.
    /// </summary>
    private const char FolderSeparator = ';';

    private static string JoinFolders(IEnumerable<string> folders) =>
        string.Join("; ", folders);

    private static List<string> SplitFolders(string? text) =>
        (text ?? string.Empty)
            .Split(FolderSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// As a standalone app, closing this window ended the process and took any running
    /// scan with it. Now that it is a child window of TagCat, closing it leaves
    /// the host running, so a scan left in flight would carry on decoding video in the
    /// background with nowhere to report to. Cancel both here instead.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _cancellation?.Cancel();
        _thumbnailCancellation?.Cancel();
        base.OnClosed(e);
    }

    /// <summary>
    /// The duplicate finder's own version, tracked separately from the host app.
    /// It used to be read from the assembly, but since this module was merged into
    /// TagCat the assembly version belongs to the host, so the two are shown
    /// side by side instead of the module silently reporting the host's number.
    /// </summary>
    internal const string DedupeVersion = "0.19";

    private static string HostAppVersion
    {
        get
        {
            var informational = Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational)) return "?";

            // The build system may append source metadata after a '+'.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
    }

    // ---- Folder selection ----------------------------------------------

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var existing = SplitFolders(FolderBox.Text);

        var dialog = new OpenFolderDialog
        {
            Title = "Choose one or more folders to scan",
            Multiselect = true
        };

        // Start where the last-listed folder is, which is the one most recently added.
        var last = existing.LastOrDefault(Directory.Exists);
        if (last is not null) dialog.InitialDirectory = last;

        if (dialog.ShowDialog(this) != true) return;

        // Replace rather than append: picking folders again is far more often "I meant
        // these instead" than "add to what is already there", and the box stays editable
        // for anyone who wants to build a list up by hand.
        var picked = dialog.FolderNames
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (picked.Count > 0) FolderBox.Text = JoinFolders(picked);
    }

    private void OnMediaTypeChanged(object sender, RoutedEventArgs e)
    {
        if (!_windowReady) return;

        // Nothing ticked would silently find zero files, which reads as "no duplicates"
        // rather than "you asked for nothing", so say so up front instead.
        bool isExpressNow = PresetExpress.IsChecked == true;
        if (SelectedExtensions(isExpressNow).Count == 0)
        {
            StatusText.Text = isExpressNow
                ? "Tick at least one of Videos, Images or Audio to scan for."
                : "Tick at least one of Videos or Images to scan for.";
        }
    }

    /// <summary>Extensions to look for, driven by the media-type checkboxes.</summary>
    private HashSet<string> SelectedExtensions(bool isExpress)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ScanVideos.IsChecked == true)
        {
            extensions.UnionWith(new[]
            {
                ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".asf",
                ".3gp", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts"
            });
        }

        if (ScanImages.IsChecked == true)
        {
            extensions.UnionWith(new[]
            {
                ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
            });
        }

        // Only Express can act on this even if it's ticked: a checksum comparison never opens
        // or decodes a file, so it works on any type including audio - the content-based
        // presets still need a real frame-hash fingerprint, which nothing here builds for audio.
        if (isExpress && ScanAudio.IsChecked == true)
        {
            extensions.UnionWith(new[]
            {
                ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a"
            });
        }

        return extensions;
    }

    // ---- Scanning -------------------------------------------------------

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        var folders = SplitFolders(FolderBox.Text);
        var missing = folders.Where(f => !Directory.Exists(f)).ToList();

        if (folders.Count == 0)
        {
            MessageBox.Show(this, "Pick at least one folder with Browse.",
                "TagCat Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (missing.Count == folders.Count)
        {
            MessageBox.Show(this,
                $"None of these folders exist:\n\n{string.Join("\n", missing)}",
                "TagCat Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (missing.Count > 0)
        {
            // Some folders are gone (a drive unplugged, say). Scanning the rest is more
            // useful than refusing outright, but it must not happen silently.
            var proceed = MessageBox.Show(this,
                $"These folders no longer exist and will be skipped:\n\n{string.Join("\n", missing)}\n\nScan the remaining {folders.Count - missing.Count}?",
                "TagCat Duplicate Finder", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.OK) return;

            folders = folders.Where(Directory.Exists).ToList();
        }

        bool isExpress = PresetExpress.IsChecked == true;
        var expressOptions = isExpress ? BuildExpressOptions() : null;

        // No "tick at least one restriction" check any more - the checksum comparison is
        // always a complete, self-sufficient answer on its own; the checkboxes only narrow
        // an already-genuine match further, so there is nothing invalid about leaving all
        // of them off.
        var extensions = SelectedExtensions(isExpress);
        if (extensions.Count == 0)
        {
            var prompt = isExpress
                ? "Tick at least one of Videos, Images or Audio to scan for."
                : "Tick at least one of Videos or Images to scan for.";
            MessageBox.Show(this, prompt,
                "TagCat Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cancellation = new CancellationTokenSource();
        SetScanning(true);

        _clusters.Clear();
        _members.Clear();
        ActionBar.Visibility = Visibility.Collapsed;
        DetailHeader.Text = "Scanning...";

        try
        {
            // Only meaningful for the content-scan path - Express has no preset baseline to
            // read, and calling this needlessly would be reaching for options that do not
            // apply to it.
            var options = isExpress ? null : BuildOptions();

            StatusText.Text = folders.Count == 1
                ? "Looking for files..."
                : $"Looking for files across {folders.Count} folders...";

            // Every control value the background work needs is captured here, on the
            // UI thread, and passed in as a plain value. Reading a control from inside
            // a Task.Run lambda is just as illegal as writing to one.
            var includeSubfolders = IncludeSubfolders.IsChecked == true;

            // Read outside the UseAdvanced guard, like UseCache: this is a discovery
            // concern rather than a fingerprinting one, so it applies in every preset.
            var includeTrash = ScanTrash.IsChecked == true;

            var includeExcluded = isExpress
                ? IncludeExcludedExpress.IsChecked == true
                : IncludeExcludedContent.IsChecked == true;

            // Session exclusions apply to every scan for the rest of this window's lifetime,
            // not just a one-time filter of what's already on screen - merged in here rather
            // than kept separate, so a second scan in the same session still honours them.
            var combinedExcludedFolders = ExcludedScanFolders.Concat(_sessionExcludedFolders).ToList();

            var files = await Task.Run(
                () => FindMediaFiles(folders, includeSubfolders, extensions, includeTrash,
                    combinedExcludedFolders, ExcludedScanFiles, includeExcluded),
                _cancellation.Token);

            if (files.Count == 0)
            {
                StatusText.Text = folders.Count == 1
                    ? "No matching files found in that folder."
                    : "No matching files found in those folders.";
                DetailHeader.Text = "Nothing to scan.";
                return;
            }

            if (isExpress)
            {
                StatusText.Text = $"Found {files.Count:N0} files. Matching by file properties...";

                _lastResult = await Task.Run(
                    () => ExpressMatcher.Scan(files, expressOptions!, _cancellation.Token),
                    _cancellation.Token);
            }
            else
            {
                StatusText.Text = $"Found {files.Count:N0} files. Examining them now...";

                using var service = VideoDeduplicationService.CreateDefault(DatabasePath);
                var progress = new Progress<ScanProgress>(ReportProgress);

                _lastResult = await service.ScanAsync(files, options!, progress, _cancellation.Token);
            }

            ShowResults(_lastResult, isExpress);
        }
        catch (OperationCanceledException)
        {
            Dispatch.OnUi(() =>
            {
                StatusText.Text = "Scan cancelled.";
                DetailHeader.Text = "Scan cancelled.";
            });
        }
        catch (Exception ex)
        {
            Dispatch.OnUi(() =>
            {
                StatusText.Text = "Scan failed.";
                MessageBox.Show(this,
                    $"{ex.Message}\n\n{ex.GetType().Name}\n\n{FirstAppFrame(ex)}",
                    "TagCat Duplicate Finder",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            SetScanning(false);
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// Pulls the first line of the stack trace that belongs to this app rather than
    /// to the framework, so an error message says which file and line to look at
    /// instead of just what went wrong.
    /// </summary>
    private static string FirstAppFrame(Exception ex)
    {
        var trace = ex.StackTrace;
        if (string.IsNullOrWhiteSpace(trace)) return string.Empty;

        foreach (var line in trace.Split('\n'))
        {
            if (line.Contains("VideoDedupe", StringComparison.Ordinal)) return line.Trim();
        }

        return trace.Split('\n')[0].Trim();
    }

    /// <summary>Opens the scan report window, which now also owns "See the full log." as a
    /// clickable link - the only remaining way to reach the log, since View Log was removed
    /// from the main screen.</summary>
    /// <summary>Shows a per-file breakdown of the last scan.</summary>
    private void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;

        var builder = new System.Text.StringBuilder();

        builder.AppendLine($"Files found:      {_lastResult.FilesScanned}");
        builder.AppendLine($"Read successfully: {_lastResult.FingerprintedCount}");
        builder.AppendLine($"Could not read:    {_lastResult.Failures.Count}");
        builder.AppendLine($"Groups found:      {_lastResult.Clusters.Count}");
        builder.AppendLine();

        // Failures first, since they are the reason someone opened this dialog.
        var ordered = _lastResult.Diagnostics
            .OrderBy(d => d.Succeeded)
            .ThenBy(d => d.FilePath)
            .Take(40);

        foreach (var diagnostic in ordered) builder.AppendLine(diagnostic.Summary);

        if (_lastResult.Diagnostics.Count > 40)
        {
            // No longer names "See the full log." here - that phrase is now a real,
            // clickable link at the bottom of the window this opens, not embedded text.
            builder.AppendLine();
            builder.AppendLine($"...and {_lastResult.Diagnostics.Count - 40} more entries.");
        }

        if (_lastResult.FingerprintedCount >= 2 && _lastResult.Clusters.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("The videos were read fine but nothing matched. If you expected a match, " +
                               "try the Thorough preset, or lower \"Confidence required\" in the advanced panel.");
        }

        var window = new ScanDetailsWindow(builder.ToString(), _lastResult.Failures.Count > 0) { Owner = this };
        window.ShowDialog();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        StatusText.Text = "Cancelling...";
    }

    /// <summary>
    /// Progress arrives from whichever worker thread happened to finish a file, so
    /// every control touched here has to be reached through the dispatcher.
    /// Posting rather than blocking keeps the workers from waiting on the UI.
    /// </summary>
    private void ReportProgress(ScanProgress p)
    {
        Dispatch.PostToUi(() =>
        {
            Progress.Value = p.Fraction;

            ProgressText.Text = p.Phase switch
            {
                ScanPhase.Discovering => "Finding files...",
                ScanPhase.Fingerprinting => $"Examining {p.Completed:N0} of {p.Total:N0}",
                ScanPhase.Matching => "Comparing videos...",
                ScanPhase.Clustering => "Grouping results...",
                ScanPhase.Complete => "Done",
                _ => string.Empty
            };

            if (p.Phase == ScanPhase.Fingerprinting && p.CurrentFile is not null)
            {
                StatusText.Text = Path.GetFileName(p.CurrentFile);
            }
        });
    }

    private void ShowResults(ScanResult result, bool isExpress) => Dispatch.OnUi(() => ShowResultsCore(result, isExpress));

    private void ShowResultsCore(ScanResult result, bool isExpress)
    {
        RefreshClusterList(result.Clusters);
        RefreshFailureList(result.Failures);

        var summary = new System.Text.StringBuilder();

        // An empty result is ambiguous on its own: it could mean there genuinely are
        // no duplicates, or that nothing was readable in the first place. Those need
        // to be told apart, so the counts are always stated.
        if (result.Clusters.Count == 0)
        {
            summary.Append(isExpress
                ? $"No matches found. {result.FingerprintedCount:N0} of {result.FilesScanned:N0} files were checked."
                : result.FingerprintedCount == 0
                    ? $"No videos could be read out of {result.FilesScanned:N0} found."
                    : $"No duplicates found. {result.FingerprintedCount:N0} of {result.FilesScanned:N0} videos were examined successfully.");
        }
        else
        {
            summary.Append(isExpress
                ? $"Found {result.Clusters.Count:N0} group(s) containing {result.TotalDuplicateFiles:N0} " +
                  $"duplicate file(s). Up to {Format.Bytes(result.TotalReclaimableBytes)} could be reclaimed. " +
                  $"{result.FingerprintedCount:N0} of {result.FilesScanned:N0} files checked."
                : $"Found {result.Clusters.Count:N0} group(s) containing {result.TotalDuplicateFiles:N0} " +
                  $"duplicate file(s). Up to {Format.Bytes(result.TotalReclaimableBytes)} could be reclaimed. " +
                  $"{result.FingerprintedCount:N0} of {result.FilesScanned:N0} videos examined.");
        }

        if (result.Failures.Count > 0)
        {
            summary.Append($"  {result.Failures.Count:N0} file(s) could not be read.");
        }

        summary.Append($"  Took {result.Elapsed.TotalSeconds:0.#}s");

        if (!isExpress && result.FingerprintsFromCache > 0)
        {
            summary.Append($", {result.FingerprintsFromCache:N0} reused from a previous scan");
        }

        summary.Append('.');

        StatusText.Text = summary.ToString();

        DetailsButton.Visibility = Visibility.Visible;

        DetailHeader.Text = result.Clusters.Count == 0
            ? "No duplicates found. Press \"What happened?\" to see what was read."
            : "Select a group on the left to review it.";
    }

    private void RefreshClusterList(IReadOnlyList<DuplicateCluster> clusters)
    {
        var sort = (DuplicateSort)SortBox.SelectedIndex;
        var sorted = DuplicateMatcher.Sort(clusters, sort);

        _clusters.Clear();
        foreach (var cluster in sorted)
        {
            _clusters.Add(new ClusterRow { Cluster = cluster });
        }
    }

    private void RefreshFailureList(IReadOnlyList<ScanFailure> failures)
    {
        _failures.Clear();
        foreach (var failure in failures.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            _failures.Add(new FailureRow { Failure = failure });
        }

        FailuresSummaryRow.Visibility = failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FailuresSummarySubtitle.Text = failures.Count == 1 ? "1 file" : $"{failures.Count} files";

        // The list a moment ago may no longer exist after a fresh scan - showing it is only
        // ever a stale answer to "what failed last time", not this time.
        if (FailureDetailScrollViewer.Visibility == Visibility.Visible) ShowMemberList();
    }

    /// <summary>Switches the right-hand panel back to the member list, hiding the
    /// could-not-read list. The two share one Grid cell and are never both visible.</summary>
    private void ShowMemberList()
    {
        FailureDetailScrollViewer.Visibility = Visibility.Collapsed;
        MemberScrollViewer.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Clicking the "Could not be read" row lists every failure at once in the same space a
    /// cluster's members would occupy - there being no separate dedicated preview area in this
    /// window, this panel is the closest equivalent. Each row still gets its own Play and
    /// Folder button, same as any other file.
    /// </summary>
    private void OnFailuresSummaryClick(object sender, MouseButtonEventArgs e)
    {
        // A failure list and a cluster's members are mutually exclusive for the same space, so
        // picking one gives up the other.
        ClusterList.SelectedItem = null;
        ActionBar.Visibility = Visibility.Collapsed;

        DetailHeader.Text = _failures.Count == 1
            ? "1 file could not be read during the scan"
            : $"{_failures.Count} files could not be read during the scan";

        MemberScrollViewer.Visibility = Visibility.Collapsed;
        FailureDetailScrollViewer.Visibility = Visibility.Visible;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        // Sorting is a display concern only, so this never triggers another scan.
        if (_lastResult is not null) RefreshClusterList(_lastResult.Clusters);
    }

    // ---- Reviewing a group ----------------------------------------------

    private async void OnClusterSelected(object sender, SelectionChangedEventArgs e)
    {
        foreach (var member in _members) member.PropertyChanged -= OnMemberTickChanged;
        _members.Clear();

        if (ClusterList.SelectedItem is not ClusterRow row)
        {
            ActionBar.Visibility = Visibility.Collapsed;
            DetailHeader.Text = "Select a group on the left to review it.";
            return;
        }

        // Selecting a cluster gives up the could-not-read list for the same reason.
        ShowMemberList();

        var cluster = row.Cluster;
        var keeper = cluster.SuggestedKeep;

        DetailHeader.Text = cluster.ContainsPartialMatches
            ? $"{cluster.Count} files — some are trimmed or partial versions, so check before deleting"
            : $"{cluster.Count} files that appear to be the same video";

        ActionBar.Visibility = Visibility.Visible;

        var rows = cluster.Members
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Duration)
            .Select(m => new MemberRow
            {
                Fingerprint = m,
                IsKeeper = ReferenceEquals(m, keeper),
                IsTicked = _tickedPaths.Contains(m.FilePath)
            })
            .ToList();

        foreach (var member in rows)
        {
            // Keeps the outside map in step with whatever the user ticks or
            // unticks here, so the state is still correct next time this file
            // shows up in a row — whether that's this group again or another one.
            member.PropertyChanged += OnMemberTickChanged;
            _members.Add(member);
        }

        // Clicking through groups quickly would otherwise leave previews from an
        // earlier selection still loading and writing into rows that are gone.
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = new CancellationTokenSource();
        var token = _thumbnailCancellation.Token;

        // Previews load after the list is on screen so selecting a group feels instant.
        try
        {
            foreach (var member in rows)
            {
                if (token.IsCancellationRequested) return;

                // The grayscale frame extractor exists for perceptual hashing, not display -
                // reusing it here is why previews used to come out black and white. This is
                // the same shell-based generator TagCat's own thumbnail grid uses,
                // which decodes in actual colour.
                var thumbnail = await Task.Run(
                    () => MediaTagger.Services.ShellThumbnailService.GetThumbnail(member.FilePath),
                    token);

                if (token.IsCancellationRequested) return;

                member.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user moves to another group mid-load.
        }
    }

    private void OnMemberTickChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MemberRow.IsTicked)) return;
        if (sender is not MemberRow row) return;

        if (row.IsTicked) _tickedPaths.Add(row.FilePath);
        else _tickedPaths.Remove(row.FilePath);
    }

    private void OnTickSuggestedClick(object sender, RoutedEventArgs e)
    {
        foreach (var member in _members) member.IsTicked = !member.IsKeeper;
    }

    private void OnUntickAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var member in _members) member.IsTicked = false;
    }

    // ---- File actions ---------------------------------------------------

    /// <summary>
    /// Set by the host so this window doesn't have to know what TagCat is. Given the
    /// files of the current duplicate group and which one was clicked, it opens the viewer -
    /// scoped to just that group, so scrolling compares the duplicates against each other
    /// rather than wandering off through an unrelated library.
    /// </summary>
    public Action<IReadOnlyList<string>, string>? OpenInViewer { get; set; }

    /// <summary>False sends files to the system default app instead of the viewer.</summary>
    public bool UseViewer { get; set; } = true;

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;

        if (UseViewer && OpenInViewer != null)
        {
            // Every file in the group currently on screen, in the order shown.
            var groupFiles = _members.Select(m => m.FilePath).ToList();
            if (groupFiles.Count == 0) groupFiles.Add(path);

            OpenInViewer(groupFiles, path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the file",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        OpenContainingFolder(path);
    }

    private void OpenContainingFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- More Options menu -------------------------------------------------

    private void MoreOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberRow row } button || button.ContextMenu == null) return;

        bool canOpenInMediaTagger = OpenInMediaTagger != null && !IsHostWindowClosed;

        // ContextMenu is its own visual tree root, so DataContext does not flow into it the
        // way it would for an ordinary sibling control - Tag is set explicitly on every item
        // here instead of relying on binding to inherit it.
        //
        // "Open Folder in TagCat" is found by its header text rather than a named
        // field: x:Name inside a DataTemplate has no field generated for it, since the
        // template is instantiated once per group member, not once for the whole window.
        foreach (var item in button.ContextMenu.Items)
        {
            if (item is not MenuItem menuItem) continue;
            menuItem.Tag = row;

            if (Equals(menuItem.Header, "Open Folder in TagCat"))
            {
                // Disabled rather than left to fail on click - a closed WPF Window can never
                // be reshown, so there is genuinely nothing this item could do once Media
                // Tagger itself has closed (which, with no ShutdownMode set and "close DF on
                // exit" off by default, it can while this window stays open).
                menuItem.IsEnabled = canOpenInMediaTagger;
            }
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OpenContainingFolder_MenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MemberRow row }) return;
        OpenContainingFolder(row.FilePath);
    }

    private async void OpenInMediaTagger_MenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MemberRow row }) return;
        if (OpenInMediaTagger == null || IsHostWindowClosed) return;

        var folder = Path.GetDirectoryName(row.FilePath);
        if (string.IsNullOrEmpty(folder)) return;

        await OpenInMediaTagger(folder, row.FilePath);
    }

    /// <summary>One Explorer window per distinct folder among the group's members, each
    /// with that group's file already highlighted in it.</summary>
    private void OpenAllFoldersInGroup_MenuClick(object sender, RoutedEventArgs e)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in _members)
        {
            var folder = Path.GetDirectoryName(member.FilePath);
            if (string.IsNullOrEmpty(folder) || !seen.Add(folder)) continue;

            OpenContainingFolder(member.FilePath);
        }
    }

    private void ExcludeFolderSession_MenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MemberRow row }) return;

        var folder = Path.GetDirectoryName(row.FilePath);
        if (string.IsNullOrEmpty(folder)) return;

        _sessionExcludedFolders.Add(folder);
        RemoveFromCurrentResults(path => IsUnderFolder(path, folder));
    }

    private void ExcludeFolderPermanent_MenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MemberRow row }) return;

        var folder = Path.GetDirectoryName(row.FilePath);
        if (string.IsNullOrEmpty(folder)) return;

        var confirm = MessageBox.Show(this,
            $"Exclude this folder from every future scan?\n\n{folder}\n\n" +
            "This can be reviewed and undone later in Settings.",
            "Exclude Folder", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        AddExcludedFolderPermanently?.Invoke(folder);
        RemoveFromCurrentResults(path => IsUnderFolder(path, folder));
    }

    private void ExcludeFilePermanent_MenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MemberRow row }) return;

        var confirm = MessageBox.Show(this,
            $"Exclude this file from every future scan?\n\n{row.FilePath}\n\n" +
            "This can be reviewed and undone later in Settings.",
            "Exclude File", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        AddExcludedFilePermanently?.Invoke(row.FilePath);
        RemoveFromCurrentResults(path => string.Equals(path, row.FilePath, StringComparison.OrdinalIgnoreCase));
    }

    private void CopyFilePath_MenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MemberRow row }) return;

        try { Clipboard.SetText(row.FilePath); }
        catch
        {
            // The clipboard can transiently fail if another app is holding it. Not worth a
            // dialog over - trying again a moment later just works.
        }
    }

    private static bool IsUnderFolder(string filePath, string folder)
    {
        var fileDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(fileDir)) return false;

        var normalizedFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedFileDir = Path.GetFullPath(fileDir).TrimEnd(Path.DirectorySeparatorChar);

        return normalizedFileDir.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
               normalizedFileDir.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes files from the results already on screen, without re-running the scan. A
    /// cluster that drops to one member is removed entirely - a single file cannot be a
    /// duplicate of anything - and the review panel resets rather than keep showing a group
    /// that may have just changed shape or vanished.
    /// </summary>
    private void RemoveFromCurrentResults(Func<string, bool> shouldRemove)
    {
        if (_lastResult == null) return;

        var filteredClusters = _lastResult.Clusters
            .Select(c => new DuplicateCluster
            {
                Members = c.Members.Where(m => !shouldRemove(m.FilePath)).ToList(),
                Matches = c.Matches
                    .Where(m => !shouldRemove(m.Left.FilePath) && !shouldRemove(m.Right.FilePath))
                    .ToList()
            })
            .Where(c => c.Members.Count > 1)
            .ToList();

        _lastResult = new ScanResult
        {
            Clusters = filteredClusters,
            Failures = _lastResult.Failures,
            FilesScanned = _lastResult.FilesScanned,
            FingerprintsFromCache = _lastResult.FingerprintsFromCache,
            Elapsed = _lastResult.Elapsed,
            Diagnostics = _lastResult.Diagnostics,
            LogPath = _lastResult.LogPath,
            FingerprintedCount = _lastResult.FingerprintedCount
        };

        RefreshClusterList(_lastResult.Clusters);

        ShowMemberList();
        ClusterList.SelectedItem = null;
        ActionBar.Visibility = Visibility.Collapsed;
        DetailHeader.Text = "Select a group on the left to review it.";
    }


    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var ticked = _members.Where(m => m.IsTicked).ToList();

        if (ticked.Count == 0)
        {
            MessageBox.Show(this, "Nothing is ticked.", "TagCat Duplicate Finder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Deleting every file in a group leaves nothing behind, which is almost never
        // what someone means to do, so it is worth stopping outright rather than warning.
        if (ticked.Count == _members.Count)
        {
            MessageBox.Show(this,
                "That would delete every file in this group, leaving no copy at all. " +
                "Untick the one you want to keep.",
                "TagCat Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var totalBytes = ticked.Sum(m => m.Fingerprint.FileSizeBytes);

        var confirm = MessageBox.Show(this,
            $"Send {ticked.Count} file(s) to the Recycle Bin?\n\n" +
            $"This frees {Format.Bytes(totalBytes)}. You can restore them from the " +
            "Recycle Bin if you change your mind.",
            "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var deleted = 0;
        var failures = new List<string>();

        foreach (var member in ticked)
        {
            try
            {
                // Recycle Bin rather than a hard delete: a false positive here would
                // otherwise be unrecoverable, and no similarity score is worth that.
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    member.FilePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                _tickedPaths.Remove(member.FilePath);

                deleted++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(member.FilePath)}: {ex.Message}");
            }
        }

        foreach (var member in ticked.Where(m => !failures.Any(f => f.StartsWith(Path.GetFileName(m.FilePath)))))
        {
            _members.Remove(member);
        }

        StatusText.Text = $"Sent {deleted} file(s) to the Recycle Bin.";

        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                "Some files could not be deleted:\n\n" + string.Join("\n", failures),
                "TagCat Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Options --------------------------------------------------------

    /// <summary>Which preset card is currently selected.</summary>
    private ScanDepth SelectedPresetDepth =>
        PresetQuick.IsChecked == true ? ScanDepth.Quick
      : PresetThorough.IsChecked == true ? ScanDepth.Thorough
      : ScanDepth.Balanced;

    /// <summary>
    /// Fires when a preset card is clicked. Moves every slider and checkbox in the
    /// advanced panel to that preset's real values, so the panel is always an
    /// honest picture of what a scan will actually do — never leaving it showing
    /// numbers left over from a preset you're no longer on.
    /// </summary>
    private void OnPresetChanged(object sender, RoutedEventArgs e) => ApplyPresetToAdvancedPanel();

    /// <summary>
    /// Set while ApplyPresetToAdvancedPanel is writing to the sliders/checkboxes below, so
    /// OnAdvancedOptionChanged can tell "the user just changed this" apart from "a preset just
    /// set this" - without it, clicking a preset card would immediately deselect itself.
    /// </summary>
    private bool _applyingPreset;

    private void ApplyPresetToAdvancedPanel()
    {
        if (!_windowReady) return;

        bool isExpress = PresetExpress.IsChecked == true;

        ContentAdvancedPanel.Visibility = isExpress ? Visibility.Collapsed : Visibility.Visible;
        ExpressOptionsPanel.Visibility = isExpress ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanelHeaderText.Text = isExpress ? "Match options" : "Advanced options";

        // Only IsEnabled changes here, never IsChecked - a WPF CheckBox keeps its checked
        // state while disabled, so ticking Audio in Express and switching to another preset
        // leaves it ticked-but-greyed, and switching back to Express restores it exactly as
        // it was left, with no extra state tracking needed for that to happen.
        ScanAudio.IsEnabled = isExpress;

        // Express has no graduated preset values to start from - its checkboxes are the only
        // settings it has, not an override of some baseline - so there is nothing to push
        // into them here the way the sliders below are pushed from the content-scan preset.
        if (isExpress) return;

        _applyingPreset = true;

        var options = DedupeOptions.For(SelectedPresetDepth);

        ThresholdSlider.Value = options.FrameHashThreshold;
        SimilaritySlider.Value = options.MinimumSimilarity;
        CropSlider.Value = options.CropInsetFraction;
        IntervalSlider.Value = options.SampleIntervalSeconds;
        DetectClips.IsChecked = options.DetectContainedClips;
        DetectLetterbox.IsChecked = options.DetectLetterbox;

        _applyingPreset = false;
    }

    /// <summary>
    /// A preset card no longer means anything once its starting values have been hand-edited -
    /// "Balanced" staying highlighted while the sliders no longer match it would be actively
    /// misleading about what a scan will actually do. Only matters once "Use these settings
    /// instead of the preset" is on; before that the sliders are just idle numbers.
    ///
    /// Two entry points because CheckBox.Checked and Slider.ValueChanged use different
    /// delegate signatures - both funnel into the same de-highlight logic.
    /// </summary>
    private void OnAdvancedOptionChanged(object sender, RoutedEventArgs e) => DeselectPresetIfAdvancedActive();

    private void OnAdvancedSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => DeselectPresetIfAdvancedActive();

    // ---- Express matching options -----------------------------------------
    //
    // None of these are required any more - the checksum comparison in ExpressMatcher is
    // always a complete answer on its own, so there is nothing to validate live here. They
    // are kept as no-op hooks rather than removed from XAML's event wiring, in case a future
    // live preview of "how many files would this restriction affect" wants them.

    private void OnExpressOptionChanged(object sender, RoutedEventArgs e) { }
    private void OnExpressOptionTextChanged(object sender, TextChangedEventArgs e) { }
    private void OnExpressOptionSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private ExpressMatchOptions BuildExpressOptions() => new()
    {
        MatchFileName = ExpressMatchName.IsChecked == true,
        MatchCreatedDate = ExpressMatchCreated.IsChecked == true,
        MatchModifiedDate = ExpressMatchModified.IsChecked == true,
        MinFileSizeBytes = ParseSizeBound(ExpressSizeMinBox.Text, ExpressSizeMinUnitCombo),
        MaxFileSizeBytes = ParseSizeBound(ExpressSizeMaxBox.Text, ExpressSizeMaxUnitCombo)
    };

    /// <summary>Empty or unparsable is "no limit on that side", not an error - matches Media
    /// Tagger's own size filter, where a box left blank is a normal, valid state.</summary>
    private static long? ParseSizeBound(string? text, ComboBox unitCombo)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) || value < 0)
            return null;

        double multiplier = unitCombo.SelectedIndex switch
        {
            0 => 1024d,
            2 => 1024d * 1024 * 1024,
            _ => 1024d * 1024
        };

        return (long)(value * multiplier);
    }

    private void DeselectPresetIfAdvancedActive()
    {
        if (!_windowReady || _applyingPreset) return;
        if (UseAdvanced.IsChecked != true) return;

        PresetQuick.IsChecked = false;
        PresetBalanced.IsChecked = false;
        PresetThorough.IsChecked = false;
    }

    private DedupeOptions BuildOptions()
    {
        var options = DedupeOptions.For(SelectedPresetDepth);

        // Starting from the preset means an untouched advanced panel behaves exactly
        // like the simple one, so ticking the box never produces a surprise.
        if (UseAdvanced.IsChecked == true)
        {
            options.FrameHashThreshold = (int)ThresholdSlider.Value;
            options.MinimumSimilarity = SimilaritySlider.Value;
            options.CropInsetFraction = CropSlider.Value;
            options.SampleIntervalSeconds = IntervalSlider.Value;
            options.DetectContainedClips = DetectClips.IsChecked == true;
            options.DetectLetterbox = DetectLetterbox.IsChecked == true;
        }

        // The app-level setting wins: with fingerprint retention off there is no cache to
        // reuse, so the per-scan checkbox cannot meaningfully turn it back on.
        options.UseCachedFingerprints = RetainFingerprints && UseCache.IsChecked == true;
        options.SortBy = (DuplicateSort)SortBox.SelectedIndex;

        return options;
    }

    private void WireSliderLabels()
    {
        void Update()
        {
            var strictness = ThresholdSlider.Value <= 7 ? "very strict"
                           : ThresholdSlider.Value <= 10 ? "balanced"
                           : ThresholdSlider.Value <= 14 ? "loose"
                           : "very loose — expect false matches";

            ThresholdLabel.Text = $"Currently {strictness}.";
            SimilarityLabel.Text = $"At least {SimilaritySlider.Value:P0} of the shorter video must match.";
            CropLabel.Text = CropSlider.Value < 0.01
                ? "Off — cropped copies will be missed."
                : $"Ignores the outer {CropSlider.Value:P0} of each edge.";
            IntervalLabel.Text = $"One frame every {IntervalSlider.Value:0.#} seconds.";
        }

        ThresholdSlider.ValueChanged += (_, _) => Update();
        SimilaritySlider.ValueChanged += (_, _) => Update();
        CropSlider.ValueChanged += (_, _) => Update();
        IntervalSlider.ValueChanged += (_, _) => Update();

        ThresholdSlider.ValueChanged += OnAdvancedSliderChanged;
        SimilaritySlider.ValueChanged += OnAdvancedSliderChanged;
        CropSlider.ValueChanged += OnAdvancedSliderChanged;
        IntervalSlider.ValueChanged += OnAdvancedSliderChanged;

        Loaded += (_, _) => Update();
    }

    private void SetScanning(bool scanning) => Dispatch.OnUi(() =>
    {
        ScanButton.IsEnabled = !scanning;
        CancelButton.IsEnabled = scanning;
        FolderBox.IsEnabled = !scanning;
        AdvancedPanel.IsEnabled = !scanning;

        // Changing what to look for mid-scan would not affect the run in progress, so
        // leaving these live would just be misleading.
        ScanVideos.IsEnabled = !scanning;
        ScanImages.IsEnabled = !scanning;
        ScanTrash.IsEnabled = !scanning;

        if (!scanning) Progress.Value = 0;
    });

    // ---- File discovery -------------------------------------------------

    private static List<string> FindMediaFiles(
        IReadOnlyList<string> folders, bool includeSubfolders, HashSet<string> extensions,
        bool includeTrash, IReadOnlyCollection<string> excludedFolders, IReadOnlyCollection<string> excludedFiles,
        bool includeExcluded)
    {
        var searchOption = includeSubfolders
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var results = new List<string>();

        // Guards against scanning the same file twice, which is easy to arrange now that
        // several folders can be listed: pick a parent and one of its own subfolders with
        // "include subfolders" on and the overlap is silent otherwise. A duplicate entry
        // would make a file cluster with itself.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var normalizedExcludedFolders = excludedFolders
            .Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar))
            .ToList();
        var excludedFileSet = new HashSet<string>(
            excludedFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        bool IsUnderExcludedFolder(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return normalizedExcludedFolders.Any(ex =>
                full.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(ex + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        // EnumerateFiles throws the moment it hits a folder it cannot read, which on a
        // real drive is common enough that walking manually and skipping is worth it.
        var pending = new Stack<string>();
        foreach (var folder in folders) pending.Push(folder);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            bool wasListedExplicitly = folders.Contains(current, StringComparer.OrdinalIgnoreCase);

            // A folder listed explicitly is always scanned, even if it is itself a trash
            // folder - the user asked for it by name. The filter only applies to folders
            // discovered by walking downwards.
            if (!includeTrash && !wasListedExplicitly && TrashPaths.Contains(current))
            {
                continue;
            }

            // Same reasoning: the exclude list is meant to stop a folder being pulled in
            // incidentally while walking downward, not to override picking it directly.
            if (!includeExcluded && !wasListedExplicitly && IsUnderExcludedFolder(current))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (TrashPaths.IsAppleDoubleSidecar(file)) continue;
                    if (!extensions.Contains(Path.GetExtension(file))) continue;
                    if (!includeExcluded && excludedFileSet.Contains(Path.GetFullPath(file))) continue;
                    if (seen.Add(Path.GetFullPath(file))) results.Add(file);
                }

                if (searchOption == SearchOption.AllDirectories)
                {
                    foreach (var sub in Directory.EnumerateDirectories(current)) pending.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }

        return results;
    }

}
