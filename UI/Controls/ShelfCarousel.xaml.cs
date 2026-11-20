using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.Controls;

/// <summary>
/// Apple Music horizontal editorial shelf carousel with clickable chevron header,
/// hover-revealed left/right paging arrows, and vertical mouse-wheel pass-through.
/// </summary>
public partial class ShelfCarousel : UserControl
{
    public static readonly DependencyProperty HeaderTitleProperty =
        DependencyProperty.Register(nameof(HeaderTitle), typeof(string), typeof(ShelfCarousel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HeaderCommandProperty =
        DependencyProperty.Register(nameof(HeaderCommand), typeof(ICommand), typeof(ShelfCarousel), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderCommandParameterProperty =
        DependencyProperty.Register(nameof(HeaderCommandParameter), typeof(object), typeof(ShelfCarousel), new PropertyMetadata(null));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ShelfCarousel), new PropertyMetadata(null));

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(ShelfCarousel), new PropertyMetadata(null));

    public string HeaderTitle
    {
        get => (string)GetValue(HeaderTitleProperty);
        set => SetValue(HeaderTitleProperty, value);
    }

    public ICommand? HeaderCommand
    {
        get => (ICommand?)GetValue(HeaderCommandProperty);
        set => SetValue(HeaderCommandProperty, value);
    }

    public object? HeaderCommandParameter
    {
        get => GetValue(HeaderCommandParameterProperty);
        set => SetValue(HeaderCommandParameterProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    private bool _isHovering;
    private bool _isSpringScrolling;
    private double _scrollTargetOffset;

    public ShelfCarousel()
    {
        InitializeComponent();
    }

    private void CarouselArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovering = true;
        UpdateArrowVisibility();
    }

    private void CarouselArea_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        UpdateArrowVisibility();
    }

    private void ShelfScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isHovering)
        {
            UpdateArrowVisibility();
        }
    }

    private void UpdateArrowVisibility()
    {
        bool canLeft = _isHovering && ShelfScroll.HorizontalOffset > 2.0;
        bool canRight = _isHovering && ShelfScroll.ScrollableWidth > 2.0 &&
                        ShelfScroll.HorizontalOffset < (ShelfScroll.ScrollableWidth - 2.0);

        ToggleArrow(LeftArrowBtn, canLeft);
        ToggleArrow(RightArrowBtn, canRight);
    }

    private static void ToggleArrow(UIElement btn, bool show)
    {
        if (show)
        {
            if (btn.Visibility != Visibility.Visible)
            {
                btn.Opacity = 0.0;
                btn.Visibility = Visibility.Visible;
            }
            SpringAnimator.SpringOpacity(btn, 1.0, response: 0.16);
        }
        else if (btn.Visibility == Visibility.Visible)
        {
            SpringAnimator.SpringOpacity(
                btn,
                0.0,
                response: 0.14,
                onComplete: () =>
                {
                    if (btn.Opacity <= 0.02)
                    {
                        btn.Visibility = Visibility.Collapsed;
                    }
                });
        }
    }

    private void ScrollLeft_Click(object sender, RoutedEventArgs e)
    {
        double step = Math.Max(360.0, ShelfScroll.ViewportWidth * 0.78);
        double baseOffset = _isSpringScrolling ? _scrollTargetOffset : ShelfScroll.HorizontalOffset;
        AnimateHorizontalScrollTo(Math.Max(0.0, baseOffset - step));
    }

    private void ScrollRight_Click(object sender, RoutedEventArgs e)
    {
        double step = Math.Max(360.0, ShelfScroll.ViewportWidth * 0.78);
        double baseOffset = _isSpringScrolling ? _scrollTargetOffset : ShelfScroll.HorizontalOffset;
        AnimateHorizontalScrollTo(Math.Min(ShelfScroll.ScrollableWidth, baseOffset + step));
    }

    private void AnimateHorizontalScrollTo(double targetOffset)
    {
        _scrollTargetOffset = Math.Clamp(targetOffset, 0.0, ShelfScroll.ScrollableWidth);
        _isSpringScrolling = true;
        SpringAnimator.SpringValue(
            ShelfScroll,
            "shelfScrollX",
            currentValue: ShelfScroll.HorizontalOffset,
            targetValue: _scrollTargetOffset,
            dampingRatio: 1.0,
            response: 0.26,
            tolerance: 0.5,
            apply: x => ShelfScroll.ScrollToHorizontalOffset(Math.Clamp(x, 0.0, ShelfScroll.ScrollableWidth)),
            onComplete: () => _isSpringScrolling = false);
    }

    private void ShelfScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Hold Shift to scroll horizontally; normal vertical wheel bubbles cleanly to parent page ScrollViewer
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            ShelfScroll.ScrollToHorizontalOffset(ShelfScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        if (!e.Handled)
        {
            e.Handled = true;
            var parentEvent = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            if (Parent is UIElement parentEl)
            {
                parentEl.RaiseEvent(parentEvent);
            }
        }
    }
}
