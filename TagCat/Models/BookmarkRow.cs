using System.ComponentModel;

namespace MediaTagger.Models
{
    /// <summary>One row in the Select Folder dropdown's bookmark list. Carries its own
    /// selection state so several can be ticked up with Ctrl+click and loaded together.</summary>
    public class BookmarkRow : INotifyPropertyChanged
    {
        public string Path { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public BookmarkRow(string path) => Path = path;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
