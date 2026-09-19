using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using VideoDedupe.Extraction;

namespace VideoDedupe
{
    /// <summary>
    /// Replaces what used to be a plain MessageBox for the scan report. A MessageBox can't
    /// host a clickable link, and "See the full log." needed to become one once the standalone
    /// View Log button was removed from the main screen - this is now the only way to reach it.
    /// </summary>
    public partial class ScanDetailsWindow : Window
    {
        /// <param name="hadFailures">Whether this scan actually hit files it could not read.
        /// The ffmpeg notice only appears when it did - suggesting a decoder to someone whose
        /// scan worked fine would be noise.</param>
        public ScanDetailsWindow(string reportText, bool hadFailures = false)
        {
            InitializeComponent();
            ReportText.Text = reportText;

            // Also hidden when ffmpeg is already present, since the scan would have used it
            // and the failures must have some other cause.
            bool suggestFfmpeg = hadFailures && !CompositeFrameExtractor.IsFfmpegAvailable();
            FfmpegNotice.Visibility = suggestFfmpeg ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Opens ffmpeg's official download page rather than fetching anything. Deliberate:
        /// ffmpeg is GPL/LGPL, and downloading or shipping the binary would bring real
        /// redistribution obligations with it. Pointing at the official page keeps TagCat
        /// clear of that entirely - the same approach Audacity used for years.
        /// </summary>
        private void OnGetFfmpegClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://ffmpeg.org/download.html")
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

        private void OnViewLogClick(object sender, RoutedEventArgs e)
        {
            var path = Diagnostics.ScanLog.LogPath;

            if (!File.Exists(path))
            {
                MessageBox.Show(this, "There's no log yet. Run a scan first.",
                    "TagCat Duplicate Finder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not open the log",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
