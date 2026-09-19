using System.ComponentModel;

namespace MediaTagger.Models
{
    /// <summary>One row in the "Remove Tags" checklist. Only tags present on at least one of the
    /// currently selected files are listed (removing a tag from a file that doesn't have it is a
    /// no-op, so there's no point cluttering the list with those). IsChecked is a plain on/off
    /// pick - unchecked by default - marking whether this tag should be removed from every
    /// selected file when "Remove Tags" is clicked. PresentCount/TotalSelected (shown via
    /// CountLabel) tell you how many of the selected files actually have the tag today.</summary>
    public class RemoveTagOption : INotifyPropertyChanged
    {
        public string Tag { get; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
        }

        private int _presentCount;
        public int PresentCount
        {
            get => _presentCount;
            set { _presentCount = value; OnPropertyChanged(nameof(PresentCount)); OnPropertyChanged(nameof(CountLabel)); }
        }

        private int _totalSelected;
        public int TotalSelected
        {
            get => _totalSelected;
            set { _totalSelected = value; OnPropertyChanged(nameof(TotalSelected)); OnPropertyChanged(nameof(CountLabel)); }
        }

        public string CountLabel => $"({PresentCount} of {TotalSelected})";

        public RemoveTagOption(string tag)
        {
            Tag = tag;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
