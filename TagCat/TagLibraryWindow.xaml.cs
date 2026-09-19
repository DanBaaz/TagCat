using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaTagger.Models;

namespace MediaTagger
{
    /// <summary>
    /// A tag library kept independently of any folder, so autocomplete can suggest tags used
    /// across the whole collection rather than only ones present in whatever's loaded right
    /// now. Changes here take effect immediately (each Add/Remove writes straight to
    /// Settings and saves), rather than being staged behind a separate Save step - what is
    /// staged is which checkboxes are ticked before that button is pressed, and it's that
    /// staging Close warns about discarding if left unapplied.
    /// </summary>
    public partial class TagLibraryWindow : Window
    {
        private enum Page { CurrentList, Add, Remove }

        private readonly MainWindow _owner;
        private Page _page = Page.Add;

        private readonly ObservableCollection<LibraryTagRow> _addRows = new();
        private readonly ObservableCollection<LibraryTagRow> _removeRows = new();

        // Shift-click range selection, one anchor per list: the index most recently clicked
        // without Shift held. A Shift-click extends from that anchor to the new click,
        // matching the same click-then-shift-click pattern the file grid already uses.
        private int? _addShiftAnchor;
        private int? _removeShiftAnchor;

        public TagLibraryWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;

            AddPageItems.ItemsSource = _addRows;
            RemovePageItems.ItemsSource = _removeRows;

            RebuildAllPages();
            ShowPage(Page.Add);
        }

        // ---------- Select All ----------

        private void AddSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool check = AddSelectAllBox.IsChecked == true;
            foreach (var row in _addRows.Where(r => r.IsEnabled)) row.IsChecked = check;
        }

        private void RemoveSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool check = RemoveSelectAllBox.IsChecked == true;
            foreach (var row in _removeRows) row.IsChecked = check;
        }

        // ---------- Shift-click range selection ----------

        private void AddRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || box.DataContext is not LibraryTagRow row) return;
            int index = _addRows.IndexOf(row);
            if (index < 0) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _addShiftAnchor is int anchor)
            {
                bool target = row.IsChecked; // whatever this click just set the clicked row to
                foreach (var i in Range(anchor, index))
                {
                    if (_addRows[i].IsEnabled) _addRows[i].IsChecked = target;
                }
            }

            _addShiftAnchor = index;
        }

        private void RemoveRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || box.DataContext is not LibraryTagRow row) return;
            int index = _removeRows.IndexOf(row);
            if (index < 0) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _removeShiftAnchor is int anchor)
            {
                bool target = row.IsChecked;
                foreach (var i in Range(anchor, index))
                    _removeRows[i].IsChecked = target;
            }

            _removeShiftAnchor = index;
        }

        private static IEnumerable<int> Range(int a, int b)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            for (int i = lo; i <= hi; i++) yield return i;
        }

        // ---------- Page switching ----------

        private void ShowCurrentListPage_Click(object sender, RoutedEventArgs e) => ShowPage(Page.CurrentList);
        private void ShowAddPage_Click(object sender, RoutedEventArgs e) => ShowPage(Page.Add);
        private void ShowRemovePage_Click(object sender, RoutedEventArgs e) => ShowPage(Page.Remove);

        private void ShowPage(Page page)
        {
            _page = page;

            CurrentListPage.Visibility = page == Page.CurrentList ? Visibility.Visible : Visibility.Collapsed;
            AddPage.Visibility = page == Page.Add ? Visibility.Visible : Visibility.Collapsed;
            RemovePage.Visibility = page == Page.Remove ? Visibility.Visible : Visibility.Collapsed;

            // The Current List page is read-only, so there is nothing for a bottom action
            // button to do there.
            ActionButton.Visibility = page == Page.CurrentList ? Visibility.Collapsed : Visibility.Visible;
            ActionButton.Content = page == Page.Add ? "Add to Library" : "Remove from Library";

            var selectedBrush = (Brush)FindResource("AccentSoftBrush");
            var unselectedBrush = (Brush)FindResource("SurfaceBrush");
            CurrentListPageButton.Background = page == Page.CurrentList ? selectedBrush : unselectedBrush;
            AddPageButton.Background = page == Page.Add ? selectedBrush : unselectedBrush;
            RemovePageButton.Background = page == Page.Remove ? selectedBrush : unselectedBrush;
        }

        // ---------- Building each page's content ----------

        /// <summary>Re-reads the library and the current folder's tags and rebuilds all three
        /// pages from scratch. Called at startup and after every committed change, so what's
        /// on screen always matches what's actually in Settings.</summary>
        private void RebuildAllPages()
        {
            var library = _owner.Settings.TagLibrary;
            var librarySet = new HashSet<string>(library, StringComparer.OrdinalIgnoreCase);
            var folderTags = _owner.GetFolderTags();
            var folderSet = new HashSet<string>(folderTags, StringComparer.OrdinalIgnoreCase);

            // Row indices and "everything selected" no longer mean anything once the lists
            // are rebuilt from scratch.
            _addShiftAnchor = null;
            _removeShiftAnchor = null;
            AddSelectAllBox.IsChecked = false;
            RemoveSelectAllBox.IsChecked = false;

            // Current List
            var sortedLibrary = library.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            CurrentListItems.ItemsSource = sortedLibrary;
            CurrentListCountText.Text = sortedLibrary.Count == 1 ? "1 tag in the library" : $"{sortedLibrary.Count} tags in the library";
            LibraryEmptyText.Visibility = sortedLibrary.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Add page: every folder tag, unchecked by default so opening this page and
            // closing again without touching anything is never treated as a pending change.
            _addRows.Clear();
            foreach (var tag in folderTags)
            {
                bool alreadyInLibrary = librarySet.Contains(tag);
                _addRows.Add(new LibraryTagRow(tag)
                {
                    IsChecked = alreadyInLibrary,
                    IsEnabled = !alreadyInLibrary,
                    Note = alreadyInLibrary ? "(already in library)" : ""
                });
            }
            NoFolderTagsText.Visibility = _addRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Remove page: every library tag, unchecked by default for the same reason.
            _removeRows.Clear();
            foreach (var tag in sortedLibrary)
            {
                _removeRows.Add(new LibraryTagRow(tag)
                {
                    IsChecked = false,
                    Note = folderSet.Contains(tag) ? "(in current folder)" : ""
                });
            }
            LibraryEmptyForRemoveText.Visibility = _removeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- Commit ----------

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_page == Page.Add) CommitAdd();
            else if (_page == Page.Remove) CommitRemove();
        }

        private void CommitAdd()
        {
            var checkedFolderTags = _addRows.Where(r => r.IsEnabled && r.IsChecked).Select(r => r.Tag);

            var manualTags = (ManualAddBox.Text ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(TagParser.SanitizeTag)
                .Where(t => t.Length > 0)
                .ToList();

            // Sanitizing above silently drops characters a tag can't contain; validating as
            // well catches the case where that leaves nothing usable and says so, rather than
            // a manually typed tag just quietly vanishing.
            if (manualTags.Count > 0 && !_owner.ValidateTags(manualTags)) return;

            var librarySet = new HashSet<string>(_owner.Settings.TagLibrary, StringComparer.OrdinalIgnoreCase);
            var toAdd = checkedFolderTags.Concat(manualTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => !librarySet.Contains(t))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (toAdd.Count == 0)
            {
                MessageBox.Show(this, "Nothing to add - check a tag or type a new one first.",
                    "Tag Library", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this,
                $"Add {toAdd.Count} tag(s) to the library?\n\n{string.Join(", ", toAdd)}",
                "Tag Library", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _owner.Settings.TagLibrary = _owner.Settings.TagLibrary
                .Concat(toAdd)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _owner.Settings.Save();

            ManualAddBox.Clear();
            RebuildAllPages();
        }

        private void CommitRemove()
        {
            var toRemove = _removeRows.Where(r => r.IsChecked).Select(r => r.Tag).ToList();

            if (toRemove.Count == 0)
            {
                MessageBox.Show(this, "Check at least one tag to remove first.",
                    "Tag Library", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this,
                $"Remove {toRemove.Count} tag(s) from the library?\n\n{string.Join(", ", toRemove.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))}\n\n" +
                "This only affects future autocomplete suggestions - no file or its existing tags are touched.",
                "Tag Library", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var removeSet = new HashSet<string>(toRemove, StringComparer.OrdinalIgnoreCase);
            _owner.Settings.TagLibrary = _owner.Settings.TagLibrary
                .Where(t => !removeSet.Contains(t))
                .ToArray();
            _owner.Settings.Save();

            RebuildAllPages();
        }

        // ---------- Close, with a warning for anything left unapplied ----------

        /// <summary>True if there is a ticked box or typed text that Add/Remove hasn't been
        /// clicked for yet. Deliberately excludes the disabled, pre-checked "already in
        /// library" rows on the Add page - those aren't a pending choice, just a display of
        /// existing state, and treating them as one would mean this warns on every close
        /// even when nothing was actually touched.</summary>
        private bool HasUnappliedSelections() =>
            _addRows.Any(r => r.IsEnabled && r.IsChecked) ||
            !string.IsNullOrWhiteSpace(ManualAddBox.Text) ||
            _removeRows.Any(r => r.IsChecked);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (HasUnappliedSelections())
            {
                var confirm = MessageBox.Show(this,
                    "You've ticked or typed something that hasn't been added or removed yet. Discard it and close?",
                    "Tag Library", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            base.OnClosing(e);
        }
    }
}
