using System.ComponentModel;

namespace MediaTagger.Models
{
    public class ExtensionFilterOption : INotifyPropertyChanged
    {
        public string Extension { get; }

        private int _count;
        public int Count
        {
            get => _count;
            set { _count = value; OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(Label)); }
        }

        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
        }

        public string Label => $"{Extension} ({Count})";

        public ExtensionFilterOption(string extension)
        {
            Extension = extension;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
