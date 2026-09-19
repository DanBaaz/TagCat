using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaTagger.Models;

namespace MediaTagger
{
    /// <summary>
    /// Full-window viewer for a single file, with next/previous navigation through whatever
    /// list it was opened from.
    ///
    /// It owns its own LibVLC instance rather than borrowing the main window's. Sharing one
    /// player between two windows would mean each one stopping the other's playback whenever
    /// a selection changed, and the preview pane keeps running independently while this is
    /// open.
    /// </summary>
    public partial class MediaViewerWindow : Window
    {
        private readonly List<MediaItem> _items;
        private int _index;

        private LibVLC? _libVLC;
        private MediaPlayer? _player;

        private bool _isMuted;
        private int _lastVolume = 100;
        private bool _draggingSlider;

        /// <summary>
        /// Set when playback reaches the end. libvlc parks the player in a finished state
        /// there, where Play() and seeking are both quietly ignored - it has to be stopped and
        /// started again first. Without tracking this, pressing play or dragging the slider
        /// after a video finished appeared to do nothing.
        /// </summary>
        private bool _endReached;

        /// <summary>A seek requested while the player wasn't running yet, applied once it is.
        /// Setting Position before playback has actually started is discarded.</summary>
        private float? _pendingSeek;

        private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        /// <summary>Chrome auto-hides after this long without mouse movement.</summary>
        private readonly DispatcherTimer _chromeHideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

        /// <summary>Polls for mouse activity. 150ms is frequent enough to feel immediate
        /// without being noticeable work.</summary>
        private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

        private WindowState _preFullScreenState = WindowState.Normal;
        private WindowStyle _preFullScreenStyle = WindowStyle.SingleBorderWindow;
        private bool _isFullScreen;

        /// <summary>
        /// Raised whenever the viewer moves to a different file, so the grid behind it can keep
        /// its highlight on whatever is actually being shown. A callback rather than the viewer
        /// reaching into the main window, which keeps this window reusable.
        /// </summary>
        public event Action<MediaItem>? CurrentItemChanged;

        public MediaViewerWindow(IEnumerable<MediaItem> items, MediaItem startAt)
        {
            InitializeComponent();

            _items = items.ToList();
            _index = Math.Max(0, _items.IndexOf(startAt));

            try
            {
                var libVlcPath = MediaTagger.Services.LibVlcLocator.Find();
                if (libVlcPath != null) Core.Initialize(libVlcPath); else Core.Initialize();
                _libVLC = new LibVLC();
                _player = new MediaPlayer(_libVLC);
                ViewerVideo.MediaPlayer = _player;

                _player.EndReached += Player_EndReached;

                // Volume set before playback has started is silently discarded by libvlc, so
                // the intended level has to be re-applied once it is actually running -
                // otherwise the button reads "unmuted" while nothing comes out.
                _player.Playing += Player_Playing;
            }
            catch
            {
                // No VLC: images still work, playable files just show a message when opened.
                _libVLC = null;
                _player = null;
            }

            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            _chromeHideTimer.Tick += ChromeHideTimer_Tick;

            _activityTimer.Tick += ActivityTimer_Tick;
            _activityTimer.Start();

            Loaded += (_, _) =>
            {
                // Keyboard focus has to be on WPF content, not the native video surface, or
                // the arrow keys and Space never reach the handler.
                RootGrid.Focusable = true;
                RootGrid.Focus();

                ShowCurrent();
            };
        }

        // ---------- Loading the current item ----------

        private void ShowCurrent()
        {
            if (_items.Count == 0) { Close(); return; }

            var item = _items[_index];

            _player?.Stop();
            ViewerPositionSlider.Value = 0;

            // A new file starts fresh, whatever state the previous one finished in.
            _endReached = false;
            _pendingSeek = null;

            ViewerFileNameText.Text = item.FileName;
            ViewerPositionText.Text = $"{_index + 1} of {_items.Count}";

            CurrentItemChanged?.Invoke(item);

            // Only meaningful with more than one file to move between.
            PrevHitZone.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            NextHitZone.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            bool playable = item.Kind is MediaKind.Video or MediaKind.Audio;

            // ViewerVideo is never hidden - the controls live inside it, so collapsing it
            // would take them with it. For images and audio it just sits idle behind them.
            ViewerImage.Visibility = item.Kind == MediaKind.Image ? Visibility.Visible : Visibility.Collapsed;
            AudioPlaceholder.Visibility = item.Kind == MediaKind.Audio ? Visibility.Visible : Visibility.Collapsed;
            BottomBar.Visibility = playable ? Visibility.Visible : Visibility.Collapsed;

            if (item.Kind == MediaKind.Image)
            {
                ViewerImage.Source = LoadFullImage(item.FullPath);
                ShowChrome();
                return;
            }

            AudioTitleText.Text = item.FileName;

            if (_player == null || _libVLC == null)
            {
                ViewerFileNameText.Text = $"{item.FileName}  —  playback unavailable (VLC failed to start)";
                return;
            }

            using var media = new Media(_libVLC, item.FullPath, FromType.FromPath);
            _player.Media = media;
            ApplyMuteState();
            _player.Play();
            ViewerPlayPauseButton.Content = "Pause";

            ShowChrome();
        }

        /// <summary>
        /// Decoded at full size rather than reusing the grid's small thumbnail, since this
        /// window is showing the image at up to full-screen scale. OnLoad so the file handle
        /// closes immediately and the file stays renameable.
        /// </summary>
        private static BitmapImage? LoadFullImage(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // ---------- Navigation ----------

        private void Previous_Click(object sender, MouseButtonEventArgs e) => Step(-1);
        private void Next_Click(object sender, MouseButtonEventArgs e) => Step(1);

        /// <summary>Wraps around at both ends, so paging through a folder never dead-ends.</summary>
        private void Step(int direction)
        {
            if (_items.Count <= 1) return;

            _index = (_index + direction + _items.Count) % _items.Count;
            ShowCurrent();
        }

        private void Player_Playing(object? sender, EventArgs e)
        {
            // Raised on a libvlc thread; touching the player from inside its own event can
            // deadlock, so hop to the UI thread first.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyMuteState();

                if (_pendingSeek is float target && _player != null)
                {
                    _player.Position = target;
                    _pendingSeek = null;
                }
            }));
        }

        private void Player_EndReached(object? sender, EventArgs e)
        {
            // VLC raises this on its own thread and forbids calling back into the player from
            // inside the event, so hop to the UI thread before touching anything.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewerLoopBox.IsChecked == true)
                {
                    // Same restart path as pressing play on a finished video - libvlc needs the
                    // stop/start, it will not simply replay from the ended state.
                    RestartFromEnd(0f);
                    return;
                }

                _endReached = true;
                ViewerPlayPauseButton.Content = "Play";

                // Left showing full so the position matches what was just watched, rather than
                // snapping to the start before anything has actually been restarted.
                ViewerPositionSlider.Value = 1;
            }));
        }

        // ---------- Keyboard ----------

        private void Viewer_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    Step(-1);
                    e.Handled = true;
                    break;

                case Key.Right:
                    Step(1);
                    e.Handled = true;
                    break;

                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;

                case Key.F11:
                    ToggleFullScreen();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    // Esc leaves full screen first, and only closes the window if not in it -
                    // otherwise there'd be no way out of full screen without also losing the
                    // viewer entirely.
                    if (_isFullScreen) ToggleFullScreen();
                    else Close();
                    e.Handled = true;
                    break;
            }

            ShowChrome();
        }

        // ---------- Playback controls ----------

        private void ViewerPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

        private void TogglePlayPause()
        {
            if (_player == null || _player.Media == null) return;

            if (_endReached)
            {
                RestartFromEnd(0f);
                return;
            }

            if (_player.IsPlaying)
            {
                _player.Pause();
                ViewerPlayPauseButton.Content = "Play";
            }
            else
            {
                _player.Play();
                ViewerPlayPauseButton.Content = "Pause";
            }
        }

        /// <summary>
        /// Brings a finished video back to life. Stop() clears libvlc's ended state so the
        /// already-loaded media can play again; the seek is deferred to the Playing event
        /// because setting Position before playback resumes is ignored.
        /// </summary>
        private void RestartFromEnd(float position)
        {
            if (_player == null) return;

            _endReached = false;
            _pendingSeek = position > 0 ? position : null;

            _player.Stop();
            _player.Play();

            ViewerPlayPauseButton.Content = "Pause";
            ViewerPositionSlider.Value = position;
        }

        private void ViewerMute_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            ApplyMuteState();
        }

        private void ViewerVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_player == null) return;
            _lastVolume = (int)e.NewValue;
            if (!_isMuted) _player.Volume = _lastVolume;
        }

        /// <summary>Mute is tracked here and applied by zeroing volume, for the same reason as
        /// the main window: LibVLCSharp's own Mute property is unreliable.</summary>
        private void ApplyMuteState()
        {
            if (_player == null) return;

            _player.Volume = _isMuted ? 0 : _lastVolume;
            ViewerMuteButton.Content = _isMuted ? "\U0001F507" : "\U0001F50A";
            ViewerVolumeSlider.IsEnabled = !_isMuted;
        }

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            if (_player == null || _draggingSlider || !_player.IsPlaying) return;

            ViewerPositionSlider.Value = _player.Position;

            var length = TimeSpan.FromMilliseconds(_player.Length);
            var at = TimeSpan.FromMilliseconds(_player.Time);
            ViewerTimeText.Text = $"{Format(at)} / {Format(length)}";

            static string Format(TimeSpan t) =>
                t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        // Same click-to-seek approach as the main window: compute the position from the mouse
        // rather than relying on WPF's built-in behaviour, which only fires reliably when the
        // click lands on the track and not the thumb.
        private void ViewerSlider_DragStart(object sender, MouseButtonEventArgs e)
        {
            _draggingSlider = true;
            SeekSliderToMouse(e.GetPosition(ViewerPositionSlider).X);
        }

        private void ViewerSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSlider && e.LeftButton == MouseButtonState.Pressed)
                SeekSliderToMouse(e.GetPosition(ViewerPositionSlider).X);
        }

        private void SeekSliderToMouse(double x)
        {
            if (ViewerPositionSlider.ActualWidth <= 0) return;
            ViewerPositionSlider.Value = Math.Clamp(x / ViewerPositionSlider.ActualWidth, 0, 1);
        }

        private void ViewerSlider_DragEnd(object sender, MouseButtonEventArgs e)
        {
            _draggingSlider = false;
            if (_player == null || _player.Media == null) return;

            var target = (float)ViewerPositionSlider.Value;

            // Seeking a finished video needs it restarted first, or the seek is ignored and the
            // slider springs back to the end.
            if (_endReached) RestartFromEnd(target);
            else _player.Position = target;
        }

        // ---------- Full screen ----------

        private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

        private void ToggleFullScreen()
        {
            if (_isFullScreen)
            {
                WindowStyle = _preFullScreenStyle;
                WindowState = _preFullScreenState;
                ResizeMode = ResizeMode.CanResize;
                _isFullScreen = false;
            }
            else
            {
                _preFullScreenStyle = WindowStyle;
                _preFullScreenState = WindowState;

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;

                // Normal first: WPF ignores a Maximized-to-Maximized change, so a window
                // already maximised would keep its title bar space without this.
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
                _isFullScreen = true;
            }

            ShowChrome();
        }

        // ---------- Auto-hiding chrome ----------

        /// <summary>
        /// Wired to the overlay grid rather than only the window. The video plays on a native
        /// surface that consumes mouse input before WPF sees it, so the window's own MouseMove
        /// stops firing the moment the pointer is over the video - which is exactly when the
        /// controls were vanishing and refusing to come back. The overlay grid is ordinary WPF
        /// content and does receive these, so it is the reliable place to listen.
        ///
        /// Shared by move, click and wheel: any of them means the person is present.
        /// </summary>
        private void Viewer_MouseMove(object sender, MouseEventArgs e) => ShowChrome();

        private void Viewer_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ShowChrome();

            // Wheel up goes to the previous file, down to the next - matching the direction a
            // scroll moves through a list elsewhere.
            Step(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        }

        // ---------- Activity detection ----------
        //
        // The WPF handlers above only fire while the pointer is over ordinary WPF content.
        // Over the video itself the native surface takes the input and neither the window nor
        // the overlay ever sees it - which is why the controls stayed hidden no matter how much
        // the mouse moved. Asking Windows directly for the cursor position sidesteps the whole
        // question of which window owns the input.

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const int VkLeftButton = 0x01;
        private const int VkRightButton = 0x02;

        private POINT _lastCursor;
        private bool _hasLastCursor;

        private void ActivityTimer_Tick(object? sender, EventArgs e)
        {
            if (!GetCursorPos(out var cursor)) return;

            // Deliberately NOT IsActive: the native video surface can hold focus, which makes
            // the window report itself inactive even while you're using it. That guard meant
            // movement was ignored entirely, while a click slipped through only because
            // clicking re-activated the window first. Checking whether the cursor is over this
            // window achieves the same thing - ignoring activity meant for another app -
            // without depending on focus at all.
            if (!IsCursorOverWindow(cursor)) return;

            bool moved = !_hasLastCursor || cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y;

            // High bit set means the button is down right now. Catches a click that happens
            // without any movement, which polling position alone would miss.
            bool clicking = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0
                         || (GetAsyncKeyState(VkRightButton) & 0x8000) != 0;

            _lastCursor = cursor;
            _hasLastCursor = true;

            if (moved || clicking) ShowChrome();
        }

        /// <summary>
        /// True if the cursor sits within this window's on-screen rectangle. Works from the
        /// window's own screen position rather than WPF hit-testing, so the video surface
        /// can't hide the answer.
        /// </summary>
        private bool IsCursorOverWindow(POINT cursor)
        {
            try
            {
                if (ActualWidth <= 0 || ActualHeight <= 0) return false;

                // PointToScreen accounts for display scaling; Left/Top would not on a
                // high-DPI monitor.
                var topLeft = PointToScreen(new Point(0, 0));
                var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));

                return cursor.X >= topLeft.X && cursor.X <= bottomRight.X
                    && cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
            }
            catch
            {
                // PointToScreen throws if the window has no source yet (mid-open or closing).
                return false;
            }
        }

        private void ShowChrome()
        {
            SetChromeVisible(true);

            // Restarting rather than just starting: each movement should extend the delay,
            // not queue up another hide.
            _chromeHideTimer.Stop();
            _chromeHideTimer.Start();
        }

        private void ChromeHideTimer_Tick(object? sender, EventArgs e)
        {
            _chromeHideTimer.Stop();

            // Hiding the controls out from under a drag would abandon the seek mid-gesture.
            if (_draggingSlider) { ShowChrome(); return; }

            SetChromeVisible(false);
        }

        private void SetChromeVisible(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;

            TopBar.Visibility = v;
            PrevGlyph.Visibility = v;
            NextGlyph.Visibility = v;

            // The bottom bar only exists for playable files in the first place.
            if (_items.Count > 0 && _items[_index].Kind is MediaKind.Video or MediaKind.Audio)
                BottomBar.Visibility = v;

            // Set on the overlay too: the window's cursor doesn't apply over the video surface.
            Cursor = visible ? Cursors.Arrow : Cursors.None;
            RootGrid.Cursor = visible ? Cursors.Arrow : Cursors.None;
        }

        // ---------- Teardown ----------

        private void CloseViewer_Click(object sender, RoutedEventArgs e) => Close();

        private void Viewer_Closed(object sender, EventArgs e)
        {
            _positionTimer.Stop();
            _chromeHideTimer.Stop();
            _activityTimer.Stop();

            if (_player != null)
            {
                _player.EndReached -= Player_EndReached;
                _player.Playing -= Player_Playing;
            }

            _player?.Stop();
            _player?.Dispose();
            _libVLC?.Dispose();
            _player = null;
            _libVLC = null;
        }
    }
}
