using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AuraAudio.UI.Controls;

/// <summary>
/// High-performance single-pass responsive virtualizing uniform grid panel implementing IScrollInfo.
/// Realizes only the visible rows (plus zero off-screen bloat), computes responsive 6/5/4/3 columns
/// and 1:1 square artwork TileHeight in a single MeasureOverride pass (zero SizeChanged 2nd pass).
/// </summary>
public sealed class VirtualizingUniformGrid : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileHeightProperty =
        DependencyProperty.Register(
            nameof(TileHeight),
            typeof(double),
            typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(210.0));

    public static readonly DependencyProperty HorizontalPaddingProperty =
        DependencyProperty.Register(
            nameof(HorizontalPadding),
            typeof(double),
            typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BottomPaddingProperty =
        DependencyProperty.Register(
            nameof(BottomPadding),
            typeof(double),
            typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(104.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxContentWidthProperty =
        DependencyProperty.Register(
            nameof(MaxContentWidth),
            typeof(double),
            typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(1360.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHorizontalGapProperty =
        DependencyProperty.Register(
            nameof(ItemHorizontalGap),
            typeof(double),
            typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CaptionReservedHeightProperty =
        DependencyProperty.Register(
            nameof(CaptionReservedHeight),
            typeof(double),
            typeof(VirtualizingUniformGrid),
            new FrameworkPropertyMetadata(92.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        private set => SetValue(TileHeightProperty, value);
    }

    public double HorizontalPadding
    {
        get => (double)GetValue(HorizontalPaddingProperty);
        set => SetValue(HorizontalPaddingProperty, value);
    }

    public double BottomPadding
    {
        get => (double)GetValue(BottomPaddingProperty);
        set => SetValue(BottomPaddingProperty, value);
    }

    public double MaxContentWidth
    {
        get => (double)GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    public double ItemHorizontalGap
    {
        get => (double)GetValue(ItemHorizontalGapProperty);
        set => SetValue(ItemHorizontalGapProperty, value);
    }

    public double CaptionReservedHeight
    {
        get => (double)GetValue(CaptionReservedHeightProperty);
        set => SetValue(CaptionReservedHeightProperty, value);
    }

    private double _extentWidth;
    private double _extentHeight;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _verticalOffset;
    private int _lastColumns = 5;
    private double _lastRowHeight = 302.0;

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extentWidth;
    public double ExtentHeight => _extentHeight;
    public double ViewportWidth => _viewportWidth;
    public double ViewportHeight => _viewportHeight;
    public double HorizontalOffset => 0.0;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }

    private void ComputeGridMetrics(double availableWidth, out double usableW, out double leftOffset, out int cols, out double cellWidth, out double artHeight, out double rowHeight)
    {
        double safeAvailW = double.IsInfinity(availableWidth) || availableWidth <= 0
            ? MaxContentWidth
            : availableWidth;
        double pad = HorizontalPadding;
        double gap = ItemHorizontalGap;

        usableW = Math.Min(MaxContentWidth, Math.Max(0.0, safeAvailW - pad * 2.0));
        leftOffset = Math.Max(pad, (safeAvailW - usableW) * 0.5);
        cols = usableW >= 1320.0 ? 6 : (usableW >= 980.0 ? 5 : (usableW >= 620.0 ? 4 : 3));
        cellWidth = Math.Max(140.0, (usableW + gap) / cols);
        artHeight = Math.Max(120.0, Math.Round(cellWidth - gap));
        rowHeight = artHeight + CaptionReservedHeight;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Accessing InternalChildren first ensures WPF connects ItemContainerGenerator to this panel.
        UIElementCollection children = InternalChildren;
        IItemContainerGenerator? generator = ItemContainerGenerator;
        ItemsControl? itemsOwner = ItemsControl.GetItemsOwner(this);
        int itemCount = itemsOwner?.Items.Count ?? 0;

        ComputeGridMetrics(
            availableSize.Width,
            out _,
            out _,
            out int cols,
            out double cellWidth,
            out double artHeight,
            out double rowHeight);

        _lastColumns = cols;
        _lastRowHeight = rowHeight;

        double safeWidth = double.IsInfinity(availableSize.Width) ? MaxContentWidth : availableSize.Width;
        if (generator == null || itemCount == 0)
        {
            if (children.Count > 0)
            {
                RemoveInternalChildRange(0, children.Count);
            }
            UpdateScrollMetrics(safeWidth, 0.0, safeWidth, double.IsInfinity(availableSize.Height) ? 0.0 : availableSize.Height);
            return new Size(safeWidth, 0.0);
        }

        int rowCount = (itemCount + cols - 1) / cols;
        double extentHeight = rowCount * rowHeight + BottomPadding;
        double viewportHeight = double.IsInfinity(availableSize.Height) ? extentHeight : availableSize.Height;

        double maxOffset = Math.Max(0.0, extentHeight - viewportHeight);
        if (_verticalOffset > maxOffset)
        {
            _verticalOffset = maxOffset;
        }
        else if (_verticalOffset < 0.0)
        {
            _verticalOffset = 0.0;
        }

        int firstVisibleRow = Math.Max(0, (int)Math.Floor(_verticalOffset / rowHeight));
        int lastVisibleRow = Math.Min(rowCount - 1, (int)Math.Floor((_verticalOffset + viewportHeight) / rowHeight));
        int firstIndex = firstVisibleRow * cols;
        int lastIndex = Math.Min(itemCount - 1, (lastVisibleRow + 1) * cols - 1);

        GeneratorPosition startPos = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        var childConstraint = new Size(cellWidth, rowHeight);

        using (generator.StartAt(startPos, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (int itemIdx = firstIndex; itemIdx <= lastIndex; itemIdx++, childIndex++)
            {
                if (generator.GenerateNext(out bool isNewlyRealized) is not UIElement child)
                {
                    continue;
                }

                if (isNewlyRealized)
                {
                    if (childIndex >= children.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }
                    generator.PrepareItemContainer(child);
                }
                else
                {
                    if (childIndex >= children.Count || !ReferenceEquals(children[childIndex], child))
                    {
                        int existingIdx = children.IndexOf(child);
                        if (existingIdx >= 0)
                        {
                            RemoveInternalChildRange(existingIdx, 1);
                            if (existingIdx < childIndex)
                            {
                                childIndex--;
                            }
                        }

                        if (childIndex >= children.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }
                    }
                }

                ApplySquareArtworkHeight(child, artHeight);
                child.Measure(childConstraint);
            }
        }

        // Recycle containers outside the active visible range
        IRecyclingItemContainerGenerator? recycling = generator as IRecyclingItemContainerGenerator;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var childPos = new GeneratorPosition(i, 0);
            int itemIdx = generator.IndexFromGeneratorPosition(childPos);
            if (itemIdx < firstIndex || itemIdx > lastIndex)
            {
                if (recycling != null)
                {
                    recycling.Recycle(childPos, 1);
                }
                else
                {
                    generator.Remove(childPos, 1);
                }
                RemoveInternalChildRange(i, 1);
            }
        }

        UpdateScrollMetrics(safeWidth, extentHeight, safeWidth, viewportHeight);

        double desiredHeight = double.IsInfinity(availableSize.Height) ? extentHeight : availableSize.Height;
        return new Size(safeWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElementCollection children = InternalChildren;
        IItemContainerGenerator? generator = ItemContainerGenerator;
        if (generator == null || children.Count == 0)
        {
            return finalSize;
        }

        ComputeGridMetrics(
            finalSize.Width,
            out _,
            out double leftOffset,
            out int cols,
            out double cellWidth,
            out _,
            out double rowHeight);

        double halfGap = ItemHorizontalGap * 0.5;
        double startX = leftOffset - halfGap;

        for (int i = 0; i < children.Count; i++)
        {
            int itemIdx = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIdx < 0) continue;

            int row = itemIdx / cols;
            int col = itemIdx % cols;
            double x = startX + col * cellWidth;
            double y = row * rowHeight - _verticalOffset;
            children[i].Arrange(new Rect(x, y, cellWidth, rowHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0 && args.Position.Index >= 0 && args.Position.Index < InternalChildren.Count)
                {
                    int count = Math.Min(args.ItemUICount, InternalChildren.Count - args.Position.Index);
                    if (count > 0)
                    {
                        RemoveInternalChildRange(args.Position.Index, count);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0)
                {
                    RemoveInternalChildRange(0, InternalChildren.Count);
                }
                _verticalOffset = 0.0;
                break;
        }

        InvalidateMeasure();
    }

    private static void ApplySquareArtworkHeight(UIElement container, double artHeight)
    {
        if (container is ContentPresenter cp)
        {
            if (VisualTreeHelper.GetChildrenCount(cp) == 0)
            {
                cp.ApplyTemplate();
            }

            if (VisualTreeHelper.GetChildrenCount(cp) > 0 &&
                VisualTreeHelper.GetChild(cp, 0) is Grid tileRoot &&
                tileRoot.Children.Count > 0 &&
                tileRoot.Children[0] is FrameworkElement artGrid &&
                (double.IsNaN(artGrid.Height) || Math.Abs(artGrid.Height - artHeight) >= 0.5))
            {
                artGrid.Height = artHeight;
            }
        }
    }

    private void UpdateScrollMetrics(double extentW, double extentH, double viewportW, double viewportH, bool forceNotify = false)
    {
        bool prevScrollable = _extentHeight > _viewportHeight + 0.5;
        bool nowScrollable = extentH > viewportH + 0.5;

        bool significantChange =
            forceNotify ||
            prevScrollable != nowScrollable ||
            Math.Abs(_viewportHeight - viewportH) > 0.5 ||
            Math.Abs(_extentHeight - extentH) > 250.0;

        _extentWidth = extentW;
        _extentHeight = extentH;
        _viewportWidth = viewportW;
        _viewportHeight = viewportH;

        if (significantChange)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        double clamped = Math.Clamp(offset, 0.0, Math.Max(0.0, _extentHeight - _viewportHeight));
        if (Math.Abs(clamped - _verticalOffset) > 0.1)
        {
            _verticalOffset = clamped;
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_verticalOffset - 48.0);
    public void LineDown() => SetVerticalOffset(_verticalOffset + 48.0);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_verticalOffset - Math.Max(48.0, _viewportHeight - 48.0));
    public void PageDown() => SetVerticalOffset(_verticalOffset + Math.Max(48.0, _viewportHeight - 48.0));
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - 120.0);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + 120.0);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement el)
        {
            int childIndex = InternalChildren.IndexOf(el);
            if (childIndex >= 0 && ItemContainerGenerator != null)
            {
                int itemIdx = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
                if (itemIdx >= 0)
                {
                    int row = itemIdx / Math.Max(1, _lastColumns);
                    double itemTop = row * _lastRowHeight;
                    double itemBottom = itemTop + _lastRowHeight;
                    if (itemTop < _verticalOffset)
                    {
                        SetVerticalOffset(itemTop);
                    }
                    else if (itemBottom > _verticalOffset + _viewportHeight)
                    {
                        SetVerticalOffset(itemBottom - _viewportHeight);
                    }
                }
            }
        }
        return rectangle;
    }
}
