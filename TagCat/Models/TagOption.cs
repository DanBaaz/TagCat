using System.ComponentModel;

namespace MediaTagger.Models
{
    public class TagOption : INotifyPropertyChanged
    {
        public string Tag { get; }

        /// <summary>True only for the synthetic "No tags" entry, which filters for files with
        /// zero tags rather than matching a literal tag string. Always pinned to the bottom
        /// of the checklist.</summary>
        public bool IsNoTagsOption { get; init; }

        private int _count;
        public int Count
        {
            get => _count;
            set { _count = value; OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(Label)); }
        }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
        }

        private bool _isAvailable = true;
        public bool IsAvailable
        {
            get => _isAvailable;
            set { _isAvailable = value; OnPropertyChanged(nameof(IsAvailable)); OnPropertyChanged(nameof(Label)); }
        }

        public string Label => IsAvailable ? $"{Tag} ({Count})" : $"{Tag} (0)";

        public TagOption(string tag)
        {
            Tag = tag;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
