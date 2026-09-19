using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MediaTagger.Models;

namespace MediaTagger.Services
{
    /// <summary>
    /// Generates thumbnails in the order the user actually needs them.
    ///
    /// The original loader walked the whole library in disk-enumeration order, one file at a
    /// time, ignoring both the current sort and the current filter. On a large folder that
    /// meant images trickling in from no discernible position while the rows on screen stayed
    /// blank, and thousands of extractions for files the filter had hidden anyway.
    ///
    /// This works from the filtered, sorted list instead, and always serves the visible range
    /// first. Scrolling or re-sorting simply changes which items are wanted next; work already
    /// done is kept, because a thumbnail is valid regardless of where the file now sits.
    /// </summary>
    public class ThumbnailLoader
    {
        /// <summary>
        /// Thumbnail extraction is IO-bound, so a few at once is markedly faster than one at
        /// a time. Kept low deliberately: the shell API hits the disk, and too many parallel
        /// requests on a spinning or external drive turn into seek thrash and get slower.
        /// </summary>
        private const int MaxConcurrency = 4;

        private readonly Dispatcher _dispatcher;
        private readonly object _gate = new();

        /// <summary>
        /// Null disables caching entirely. Settable rather than fixed at construction, so
        /// turning the Settings option on or off applies to a scan already in progress, and
        /// pointing it at a different folder swaps which database gets written to.
        /// </summary>
        public ThumbnailCacheStore? Cache { get; set; }

        /// <summary>
        /// Raised, at most once per file type per session, when a thumbnail failed on a format
        /// that needs a Microsoft Store extension package. An event rather than a dialog here
        /// because this runs on a worker thread, and because the loader has no business
        /// deciding how the UI wants to raise it.
        /// </summary>
        public event Action<string>? CodecSuggested;

        /// <summary>The filtered, sorted list, in the order it appears on screen.</summary>
        private List<MediaItem> _workingSet = new();

        private int _priorityFirst = -1;
        private int _priorityLast = -1;

        /// <summary>Items already done or in flight, so a re-sort never redoes work.</summary>
        private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cancellation;
        private Task? _worker;

        public ThumbnailLoader(Dispatcher dispatcher) => _dispatcher = dispatcher;

        /// <summary>
        /// Replaces what should be loaded and in what order. Called whenever the display list
        /// changes: new folder, filter change, sort change.
        /// </summary>
        public void SetWorkingSet(IEnumerable<MediaItem> items, bool resetHandled)
        {
            lock (_gate)
            {
                _workingSet = items.ToList();

                // Only a genuinely new library invalidates what has been done. A filter or
                // sort change is the same files in a different order, so the cache stands.
                if (resetHandled) _handled.Clear();
            }

            Start();
        }

        /// <summary>
        /// The index range currently on screen, reported by the panel. Items inside it are
        /// loaded before anything else.
        /// </summary>
        public void SetPriorityRange(int first, int last)
        {
            lock (_gate)
            {
                if (_priorityFirst == first && _priorityLast == last) return;
                _priorityFirst = first;
                _priorityLast = last;
            }

            Start();
        }

        public void Stop()
        {
            lock (_gate)
            {
                _cancellation?.Cancel();
                _cancellation = null;
                _worker = null;
            }
        }

        /// <summary>Clears everything, for a folder reload.</summary>
        public void Reset()
        {
            Stop();
            lock (_gate)
            {
                _workingSet = new List<MediaItem>();
                _handled.Clear();
                _priorityFirst = _priorityLast = -1;
            }
        }

        private void Start()
        {
            lock (_gate)
            {
                // A worker is already draining the queue; it will pick up the new priorities
                // on its next iteration, so there is nothing to restart.
                if (_worker is { IsCompleted: false }) return;

                _cancellation?.Cancel();
                _cancellation = new CancellationTokenSource();
                var token = _cancellation.Token;

                _worker = Task.Run(() => RunAsync(token), token);
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            using var throttle = new SemaphoreSlim(MaxConcurrency);
            var inFlight = new List<Task>();

            while (!token.IsCancellationRequested)
            {
                var next = TakeNext();
                if (next == null) break; // nothing left to do

                await throttle.WaitAsync(token).ConfigureAwait(false);

                inFlight.Add(Task.Run(async () =>
                {
                    try
                    {
                        var cache = Cache;

                        // A cache hit skips the shell call entirely - the whole point of this.
                        var thumb = cache != null
                            ? await cache.TryGetAsync(next.FullPath, token).ConfigureAwait(false)
                            : null;
                        bool fromCache = thumb != null;

                        thumb ??= ShellThumbnailService.GetThumbnail(next.FullPath);

                        if (thumb == null)
                        {
                            // Windows returns nothing rather than an error when the codec for
                            // a format is missing, so an empty result on a format known to
                            // need a Store extension is the only signal available that this
                            // is a fixable codec problem rather than a corrupt file.
                            if (CodecHelper.ShouldSuggestCodecFor(next.FullPath))
                                CodecSuggested?.Invoke(next.FullPath);
                            return;
                        }

                        if (token.IsCancellationRequested) return;

                        // GetThumbnail returns a frozen BitmapSource, so it is safe to hand
                        // across threads; the assignment still has to happen on the UI thread
                        // because it raises PropertyChanged for the binding.
                        await _dispatcher.InvokeAsync(() => next.Thumbnail = thumb);

                        // Only worth writing back what was not already there.
                        if (cache != null && !fromCache)
                            await cache.StoreAsync(next.FullPath, thumb, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A file that cannot be thumbnailed keeps its placeholder. It is
                        // already marked handled, so it will not be retried in a loop.
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }, token));

                // Keep the completed-task list from growing over a long run.
                inFlight.RemoveAll(t => t.IsCompleted);
            }

            try { await Task.WhenAll(inFlight).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// The next most useful item: anything visible that still lacks a thumbnail, else the
        /// first such item in display order. Marks it handled before returning so concurrent
        /// workers cannot pick the same file twice.
        /// </summary>
        private MediaItem? TakeNext()
        {
            lock (_gate)
            {
                var set = _workingSet;

                if (_priorityFirst >= 0)
                {
                    int first = Math.Max(0, _priorityFirst);
                    int last = Math.Min(set.Count - 1, _priorityLast);

                    for (int i = first; i <= last; i++)
                    {
                        if (TryClaim(set[i], out var claimed)) return claimed;
                    }
                }

                foreach (var item in set)
                {
                    if (TryClaim(item, out var claimed)) return claimed;
                }

                return null;
            }
        }

        private bool TryClaim(MediaItem item, out MediaItem? claimed)
        {
            claimed = null;

            if (item.Thumbnail != null) return false;
            if (!_handled.Add(item.FullPath)) return false;

            claimed = item;
            return true;
        }
    }
}
