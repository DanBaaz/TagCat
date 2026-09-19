using System.ComponentModel;

namespace MediaTagger.Models
{
    /// <summary>
    /// One row in the "Exclude tags" list. Kept separate from TagOption rather than adding a
    /// third state to it: the include list and the exclude list are independent choices, and a
    /// tag can legitimately appear unticked in both.
    /// </summary>
    public class ExcludeTagOption : INotifyPropertyChanged
    {
        public string Tag { get; }

        /// <summary>True only for the synthetic "No tags" entry, which excludes files that have
        /// no tags at all - i.e. "only show files I've actually tagged".</summary>
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

        /// <summary>False greys the row out - set when the same tag is ticked as an include,
        /// where excluding it as well would contradict.</summary>
        public bool IsAvailable
        {
            get => _isAvailable;
            set { _isAvailable = value; OnPropertyChanged(nameof(IsAvailable)); }
        }

        public string Label => $"{Tag} ({Count})";

        public ExcludeTagOption(string tag) => Tag = tag;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
