using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MediaTagger.Controls
{
    /// <summary>
    /// A wrapping panel that only creates the tiles currently on screen.
    ///
    /// WPF ships no virtualizing wrap panel: VirtualizingStackPanel only stacks in one
    /// direction, and plain WrapPanel realizes every item. With a few thousand files that
    /// means a few thousand decoded thumbnails and UI element trees held at once, which is
    /// slow to open and eventually runs out of memory.
    ///
    /// This assumes every item is the same size, which is what makes it simple enough to
    /// be trustworthy: with a uniform cell, the visible range is arithmetic rather than a
    /// measure pass over everything. The tile template therefore fixes its width and
    /// height. If items were allowed to size themselves, this panel would be wrong.
    ///
    /// Scrolling is per-pixel via IScrollInfo rather than per-item, so the scrollbar behaves
    /// normally and the mouse wheel does not jump a whole row at a time.
    /// </summary>
    public class VisibleRangeEventArgs : EventArgs
    {
        public int First { get; }
        public int Last { get; }

        public VisibleRangeEventArgs(int first, int last)
        {
            First = first;
            Last = last;
        }
    }

    public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double ItemWidth
        {
            get => (double)GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        public double ItemHeight
        {
            get => (double)GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        /// <summary>
        /// Raised when the on-screen index range changes, so thumbnail generation can follow
        /// the viewport instead of grinding through the library in storage order.
        /// </summary>
        public event EventHandler<VisibleRangeEventArgs>? VisibleRangeChanged;

        private int _reportedFirst = -1;
        private int _reportedLast = -1;

        private Size _extent;
        private Size _viewport;
        private Point _offset;

        private int ItemCount
        {
            get
            {
                var owner = ItemsControl.GetItemsOwner(this);
                return owner?.Items.Count ?? 0;
            }
        }

        private int ColumnsFor(double width) =>
            Math.Max(1, (int)Math.Floor(width / Math.Max(1.0, ItemWidth)));

        protected override Size MeasureOverride(Size availableSize)
        {
            // Can happen mid-swap: ItemsPanel was reassigned (Settings toggling
            // virtualization) while a layout pass for this exact panel instance was already
            // queued. By the time it fires, this panel is no longer the active items host,
            // and ItemContainerGenerator's internal state can no longer be trusted - there is
            // nothing valid left to measure, so return quietly instead of touching it.
            if (ItemContainerGenerator == null || ItemsControl.GetItemsOwner(this) == null)
                return new Size(0, 0);

            // Infinite height means this panel is not being measured against a real viewport,
            // which happens when a ScrollViewer wraps the ItemsControl from outside instead of
            // living inside its template. The panel would then think the viewport is one row
            // tall and realize almost nothing, so fall back to a usable height rather than
            // rendering a near-empty grid.
            var width = double.IsInfinity(availableSize.Width) ? ItemWidth * 4 : availableSize.Width;
            var height = double.IsInfinity(availableSize.Height)
                ? Math.Max(ItemHeight * 8, SystemParameters.PrimaryScreenHeight)
                : availableSize.Height;

            var count = ItemCount;
            var columns = ColumnsFor(width);
            var rows = (int)Math.Ceiling(count / (double)columns);

            var extent = new Size(columns * ItemWidth, rows * ItemHeight);
            var viewport = new Size(width, height);

            if (extent != _extent || viewport != _viewport)
            {
                _extent = extent;
                _viewport = viewport;
                ScrollOwner?.InvalidateScrollInfo();
            }

            // Clamp after a resize, so shrinking the window cannot leave the view scrolled
            // past the end of a now-shorter extent.
            CoerceOffset();

            var (first, last) = VisibleRange(columns, count);
            RealizeRange(first, last);
            ReportVisibleRange(first, last);

            var childConstraint = new Size(ItemWidth, ItemHeight);
            foreach (UIElement child in InternalChildren) child.Measure(childConstraint);

            return viewport;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var columns = ColumnsFor(finalSize.Width);
            var count = ItemCount;
            var (first, _) = VisibleRange(columns, count);

            var generator = ItemContainerGenerator;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];

                // Map the child back to its item index rather than assuming children are in
                // order: the generator may recycle and reorder containers.
                var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                if (itemIndex < 0) itemIndex = first + i;

                var row = itemIndex / columns;
                var column = itemIndex % columns;

                child.Arrange(new Rect(
                    column * ItemWidth - _offset.X,
                    row * ItemHeight - _offset.Y,
                    ItemWidth,
                    ItemHeight));
            }

            return finalSize;
        }

        /// <summary>
        /// Index range to realize: everything intersecting the viewport, plus one row either
        /// side so a scroll of a few pixels does not expose an empty band before the next
        /// measure pass catches up.
        /// </summary>
        private (int First, int Last) VisibleRange(int columns, int count)
        {
            if (count == 0) return (0, -1);

            var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / ItemHeight) - 1);
            var visibleRows = (int)Math.Ceiling(_viewport.Height / ItemHeight) + 2;

            var first = firstRow * columns;
            var last = Math.Min(count - 1, (firstRow + visibleRows) * columns - 1);

            return (Math.Min(first, count - 1), last);
        }

        private void ReportVisibleRange(int first, int last)
        {
            if (first == _reportedFirst && last == _reportedLast) return;

            _reportedFirst = first;
            _reportedLast = last;
            VisibleRangeChanged?.Invoke(this, new VisibleRangeEventArgs(first, last));
        }

        private void RealizeRange(int first, int last)
        {
            var generator = ItemContainerGenerator;
            if (generator == null) return; // detached panel instance; nothing to realize into

            if (last < first)
            {
                CleanUpItems(0, -1);
                return;
            }

            var startPos = generator.GeneratorPositionFromIndex(first);

            // When the requested item is already realized, offset 0 means "this container",
            // so generation must begin at the following one to avoid duplicating it.
            int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

            using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool isNewlyRealized);

                    if (isNewlyRealized)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);

                        generator.PrepareItemContainer(child);
                    }
                }
            }

            CleanUpItems(first, last);
        }

        /// <summary>Releases containers outside the visible range back to the generator.</summary>
        private void CleanUpItems(int first, int last)
        {
            var generator = ItemContainerGenerator;

            for (int i = InternalChildren.Count - 1; i >= 0; i--)
            {
                var position = new GeneratorPosition(i, 0);
                int itemIndex = generator.IndexFromGeneratorPosition(position);

                if (itemIndex < first || itemIndex > last)
                {
                    generator.Remove(position, 1);
                    RemoveInternalChildRange(i, 1);
                }
            }
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            // The item list changed underneath us, so every cached container is suspect.
            switch (args.Action)
            {
                case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    break;

                case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                    RemoveInternalChildRange(0, InternalChildren.Count);
                    // A reset usually means a new folder or a changed filter; showing the
                    // middle of the old list would be meaningless.
                    _offset = new Point(0, 0);
                    break;
            }

            InvalidateMeasure();
        }

        // ---- IScrollInfo ----------------------------------------------------

        public bool CanVerticallyScroll { get; set; } = true;

        /// <summary>Always false: the panel wraps to fit the width, so there is never
        /// anything to scroll to horizontally.</summary>
        public bool CanHorizontallyScroll
        {
            get => false;
            set { }
        }

        public double ExtentWidth => _extent.Width;
        public double ExtentHeight => _extent.Height;
        public double ViewportWidth => _viewport.Width;
        public double ViewportHeight => _viewport.Height;
        public double HorizontalOffset => _offset.X;
        public double VerticalOffset => _offset.Y;
        public ScrollViewer? ScrollOwner { get; set; }

        private void CoerceOffset()
        {
            var maxY = Math.Max(0, _extent.Height - _viewport.Height);
            var y = Math.Max(0, Math.Min(_offset.Y, maxY));
            if (Math.Abs(y - _offset.Y) > 0.001)
            {
                _offset.Y = y;
                ScrollOwner?.InvalidateScrollInfo();
            }
        }

        private void SetVerticalOffsetInternal(double offset)
        {
            var maxY = Math.Max(0, _extent.Height - _viewport.Height);
            var y = Math.Max(0, Math.Min(offset, maxY));

            if (Math.Abs(y - _offset.Y) < 0.001) return;

            _offset.Y = y;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        public void SetVerticalOffset(double offset) => SetVerticalOffsetInternal(offset);
        public void SetHorizontalOffset(double offset) { }

        // A "line" is one tile row, which is what makes wheel scrolling feel right here.
        public void LineUp() => SetVerticalOffsetInternal(_offset.Y - ItemHeight);
        public void LineDown() => SetVerticalOffsetInternal(_offset.Y + ItemHeight);
        public void PageUp() => SetVerticalOffsetInternal(_offset.Y - _viewport.Height);
        public void PageDown() => SetVerticalOffsetInternal(_offset.Y + _viewport.Height);

        // Three rows per wheel notch, matching the usual Windows feel.
        public void MouseWheelUp() => SetVerticalOffsetInternal(_offset.Y - ItemHeight * 3);
        public void MouseWheelDown() => SetVerticalOffsetInternal(_offset.Y + ItemHeight * 3);

        public void LineLeft() { }
        public void LineRight() { }
        public void PageLeft() { }
        public void PageRight() { }
        public void MouseWheelLeft() { }
        public void MouseWheelRight() { }

        /// <summary>
        /// Scrolls a child into view. Needed for keyboard navigation: arrowing past the
        /// bottom of the visible tiles has to bring the next row up.
        /// </summary>
        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            var child = visual as UIElement;
            if (child == null) return rectangle;

            int index = InternalChildren.IndexOf(child);
            if (index < 0) return rectangle;

            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(index, 0));
            if (itemIndex < 0) return rectangle;

            var columns = ColumnsFor(_viewport.Width);
            var rowTop = itemIndex / columns * ItemHeight;
            var rowBottom = rowTop + ItemHeight;

            if (rowTop < _offset.Y) SetVerticalOffsetInternal(rowTop);
            else if (rowBottom > _offset.Y + _viewport.Height)
                SetVerticalOffsetInternal(rowBottom - _viewport.Height);

            return rectangle;
        }
    }
}
