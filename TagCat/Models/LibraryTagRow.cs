using System.ComponentModel;

namespace MediaTagger.Models
{
    /// <summary>One row in the Tag Library window's Add or Remove list.</summary>
    public class LibraryTagRow : INotifyPropertyChanged
    {
        public string Tag { get; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
        }

        /// <summary>False greys the row out - used on the Add page for a folder tag that's
        /// already in the library, since there's nothing to do for it.</summary>
        public bool IsEnabled { get; init; } = true;

        /// <summary>Small annotation shown after the tag, e.g. "(already in library)" or
        /// "(in current folder)". Empty for no annotation.</summary>
        public string Note { get; init; } = "";

        public LibraryTagRow(string tag) => Tag = tag;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
