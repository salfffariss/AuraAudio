using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Attached behaviors implementing fluid Apple-style physical interaction rules for WPF:
/// immediate pointer-down press feedback, hover lift springs, and 1:1 direct slider scrubbing.
/// </summary>
public static class FluidBehaviors
{
    public static readonly DependencyProperty EnablePressScaleProperty =
        DependencyProperty.RegisterAttached(
            "EnablePressScale",
            typeof(bool),
            typeof(FluidBehaviors),
            new PropertyMetadata(false, OnEnablePressScaleChanged));

    public static bool GetEnablePressScale(DependencyObject obj) => (bool)obj.GetValue(EnablePressScaleProperty);
    public static void SetEnablePressScale(DependencyObject obj, bool value) => obj.SetValue(EnablePressScaleProperty, value);

    public static readonly DependencyProperty PressScaleProperty =
        DependencyProperty.RegisterAttached(
            "PressScale",
            typeof(double),
            typeof(FluidBehaviors),
            new PropertyMetadata(0.97));

    public static double GetPressScale(DependencyObject obj) => (double)obj.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject obj, double value) => obj.SetValue(PressScaleProperty, value);

    public static readonly DependencyProperty EnableHoverLiftProperty =
        DependencyProperty.RegisterAttached(
            "EnableHoverLift",
            typeof(bool),
            typeof(FluidBehaviors),
            new PropertyMetadata(false, OnEnableHoverLiftChanged));

    public static bool GetEnableHoverLift(DependencyObject obj) => (bool)obj.GetValue(EnableHoverLiftProperty);
    public static void SetEnableHoverLift(DependencyObject obj, bool value) => obj.SetValue(EnableHoverLiftProperty, value);

    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached(
            "HoverScale",
            typeof(double),
            typeof(FluidBehaviors),
            new PropertyMetadata(1.02));

    public static double GetHoverScale(DependencyObject obj) => (double)obj.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject obj, double value) => obj.SetValue(HoverScaleProperty, value);

    public static readonly DependencyProperty EnableDirectScrubProperty =
        DependencyProperty.RegisterAttached(
            "EnableDirectScrub",
            typeof(bool),
            typeof(FluidBehaviors),
            new PropertyMetadata(false, OnEnableDirectScrubChanged));

    public static bool GetEnableDirectScrub(DependencyObject obj) => (bool)obj.GetValue(EnableDirectScrubProperty);
    public static void SetEnableDirectScrub(DependencyObject obj, bool value) => obj.SetValue(EnableDirectScrubProperty, value);

    private static readonly DependencyProperty IsTrackScrubbingProperty =
        DependencyProperty.RegisterAttached(
            "IsTrackScrubbing",
            typeof(bool),
            typeof(FluidBehaviors),
            new PropertyMetadata(false));

    private static readonly DependencyProperty IsThumbDraggingProperty =
        DependencyProperty.RegisterAttached(
            "IsThumbDragging",
            typeof(bool),
            typeof(FluidBehaviors),
            new PropertyMetadata(false));

    private static void OnEnablePressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;

        el.PreviewMouseLeftButtonDown -= Press_OnMouseDown;
        el.PreviewMouseLeftButtonUp -= Press_OnMouseUp;
        el.MouseLeave -= Press_OnMouseLeave;
        el.LostMouseCapture -= Press_OnLostCapture;

        if ((bool)e.NewValue)
        {
            SpringAnimator.EnsureTransformGroup(el);
            el.PreviewMouseLeftButtonDown += Press_OnMouseDown;
            el.PreviewMouseLeftButtonUp += Press_OnMouseUp;
            el.MouseLeave += Press_OnMouseLeave;
            el.LostMouseCapture += Press_OnLostCapture;
        }
    }

    private static void Press_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement el && el.IsEnabled && !SpringAnimator.IsReducedMotion)
        {
            SpringAnimator.SpringScale(el, GetPressScale(el), dampingRatio: 1.0, response: 0.12);
        }
    }

    private static void Press_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement el)
        {
            SpringAnimator.SpringScale(el, 1.0, dampingRatio: 1.0, response: 0.22);
        }
    }

    private static void Press_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is UIElement el)
        {
            SpringAnimator.SpringScale(el, 1.0, dampingRatio: 1.0, response: 0.22);
        }
    }

    private static void Press_OnLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is UIElement el)
        {
            SpringAnimator.SpringScale(el, 1.0, dampingRatio: 1.0, response: 0.22);
        }
    }

    private static void OnEnableHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;

        el.MouseEnter -= Lift_OnMouseEnter;
        el.MouseLeave -= Lift_OnMouseLeave;
        el.PreviewMouseLeftButtonDown -= Lift_OnMouseDown;
        el.PreviewMouseLeftButtonUp -= Lift_OnMouseUp;

        if ((bool)e.NewValue)
        {
            SpringAnimator.EnsureTransformGroup(el);
            el.MouseEnter += Lift_OnMouseEnter;
            el.MouseLeave += Lift_OnMouseLeave;
            el.PreviewMouseLeftButtonDown += Lift_OnMouseDown;
            el.PreviewMouseLeftButtonUp += Lift_OnMouseUp;
        }
    }

    private static void Lift_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is UIElement el && el.IsEnabled && !SpringAnimator.IsReducedMotion)
        {
            SpringAnimator.SpringScale(el, GetHoverScale(el), dampingRatio: 1.0, response: 0.22);
            SpringAnimator.SpringTranslateY(el, -2.0, dampingRatio: 1.0, response: 0.22);
        }
    }

    private static void Lift_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is UIElement el)
        {
            SpringAnimator.SpringScale(el, 1.0, dampingRatio: 1.0, response: 0.24);
            SpringAnimator.SpringTranslateY(el, 0.0, dampingRatio: 1.0, response: 0.24);
        }
    }

    private static void Lift_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement el && el.IsEnabled && !SpringAnimator.IsReducedMotion)
        {
            SpringAnimator.SpringScale(el, 0.97, dampingRatio: 1.0, response: 0.12);
            SpringAnimator.SpringTranslateY(el, 0.0, dampingRatio: 1.0, response: 0.12);
        }
    }

    private static void Lift_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement el && el.IsEnabled)
        {
            bool lift = el.IsMouseOver && !SpringAnimator.IsReducedMotion;
            double target = lift ? GetHoverScale(el) : 1.0;
            double targetY = lift ? -2.0 : 0.0;
            SpringAnimator.SpringScale(el, target, dampingRatio: 1.0, response: 0.22);
            SpringAnimator.SpringTranslateY(el, targetY, dampingRatio: 1.0, response: 0.22);
        }
    }

    private static void OnEnableDirectScrubChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        slider.PreviewMouseLeftButtonDown -= Scrub_OnMouseDown;
        slider.PreviewMouseMove -= Scrub_OnMouseMove;
        slider.PreviewMouseLeftButtonUp -= Scrub_OnMouseUp;
        slider.LostMouseCapture -= Scrub_OnLostCapture;
        slider.MouseEnter -= Scrub_OnVisualStateChanged;
        slider.MouseLeave -= Scrub_OnVisualStateChanged;
        slider.GotKeyboardFocus -= Scrub_OnVisualStateChanged;
        slider.LostKeyboardFocus -= Scrub_OnVisualStateChanged;
        slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Scrub_OnThumbDragStarted));
        slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Scrub_OnThumbDragCompleted));

        if ((bool)e.NewValue)
        {
            slider.IsMoveToPointEnabled = true;
            slider.PreviewMouseLeftButtonDown += Scrub_OnMouseDown;
            slider.PreviewMouseMove += Scrub_OnMouseMove;
            slider.PreviewMouseLeftButtonUp += Scrub_OnMouseUp;
            slider.LostMouseCapture += Scrub_OnLostCapture;
            slider.MouseEnter += Scrub_OnVisualStateChanged;
            slider.MouseLeave += Scrub_OnVisualStateChanged;
            slider.GotKeyboardFocus += Scrub_OnVisualStateChanged;
            slider.LostKeyboardFocus += Scrub_OnVisualStateChanged;
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Scrub_OnThumbDragStarted), handledEventsToo: true);
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Scrub_OnThumbDragCompleted), handledEventsToo: true);
        }
    }

    private static void Scrub_OnVisualStateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            UpdateElasticScrubberVisuals(slider);
        }
    }

    private static void Scrub_OnThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is Slider slider)
        {
            slider.SetValue(IsThumbDraggingProperty, true);
            UpdateElasticScrubberVisuals(slider);
        }
    }

    private static void Scrub_OnThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is Slider slider)
        {
            slider.SetValue(IsThumbDraggingProperty, false);
            UpdateElasticScrubberVisuals(slider);
        }
    }

    private static void Scrub_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled) return;
        if (IsInsideThumb(e.OriginalSource as DependencyObject))
        {
            UpdateElasticScrubberVisuals(slider);
            return;
        }

        slider.SetValue(IsTrackScrubbingProperty, true);
        UpdateSliderValueFromPointer(slider, e);
        slider.CaptureMouse();
        UpdateElasticScrubberVisuals(slider);
    }

    private static void Scrub_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!(bool)slider.GetValue(IsTrackScrubbingProperty)) return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSliderValueFromPointer(slider, e);
        }
        else
        {
            EndTrackScrub(slider);
        }
    }

    private static void Scrub_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && (bool)slider.GetValue(IsTrackScrubbingProperty))
        {
            UpdateSliderValueFromPointer(slider, e);
            EndTrackScrub(slider);
        }
    }

    private static void Scrub_OnLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is Slider slider)
        {
            slider.SetValue(IsTrackScrubbingProperty, false);
            UpdateElasticScrubberVisuals(slider);
        }
    }

    private static void EndTrackScrub(Slider slider)
    {
        slider.SetValue(IsTrackScrubbingProperty, false);
        if (slider.IsMouseCaptured)
        {
            slider.ReleaseMouseCapture();
        }
        UpdateElasticScrubberVisuals(slider);
    }

    /// <summary>
    /// Spring-driven elastic scrubber expansion (3px <-> 6px track, smooth thumb opacity &amp; active drag scale)
    /// that stays expanded throughout pointer-captured dragging even if the cursor leaves the slider bounds.
    /// </summary>
    public static void UpdateElasticScrubberVisuals(Slider slider)
    {
        if (slider.Template == null) return;

        bool isTrackScrubbing = (bool)slider.GetValue(IsTrackScrubbingProperty);
        bool isThumbDragging = (bool)slider.GetValue(IsThumbDraggingProperty);
        bool isDragging = isTrackScrubbing || isThumbDragging;
        bool isExpanded = slider.IsMouseOver || slider.IsKeyboardFocused || isDragging;

        if (slider.Template.FindName("ElasticTrackBackground", slider) is Border trackBg)
        {
            double targetHeight = isExpanded ? 6.0 : 3.0;
            SpringAnimator.SpringHeight(trackBg, targetHeight, dampingRatio: 1.0, response: 0.16);
        }

        if (slider.Template.FindName("DecreaseBarButton", slider) is RepeatButton decreaseBtn &&
            decreaseBtn.Template?.FindName("ElasticFillBar", decreaseBtn) is Border fillBar)
        {
            double targetHeight = isExpanded ? 6.0 : 3.0;
            SpringAnimator.SpringHeight(fillBar, targetHeight, dampingRatio: 1.0, response: 0.16);
        }

        if (slider.Template.FindName("ThumbControl", slider) is Thumb thumb)
        {
            bool isElasticStyle = slider.Template.FindName("ElasticTrackBackground", slider) != null;
            if (isElasticStyle)
            {
                SpringAnimator.SpringOpacity(thumb, isExpanded ? 1.0 : 0.0, response: 0.14);
            }
            else
            {
                SpringAnimator.SpringOpacity(thumb, isExpanded ? 1.0 : 0.88, response: 0.14);
            }

            double targetThumbScale = (!SpringAnimator.IsReducedMotion && isDragging) ? 1.15 : 1.0;
            SpringAnimator.SpringScale(thumb, targetThumbScale, dampingRatio: 1.0, response: 0.14);
        }
    }

    private static void UpdateSliderValueFromPointer(Slider slider, MouseEventArgs e)
    {
        if (slider.Template?.FindName("PART_Track", slider) is Track track && track.ActualWidth > 0)
        {
            double val = track.ValueFromPoint(e.GetPosition(track));
            slider.Value = Math.Clamp(val, slider.Minimum, slider.Maximum);
        }
        else if (slider.ActualWidth > 0)
        {
            double ratio = Math.Clamp(e.GetPosition(slider).X / slider.ActualWidth, 0.0, 1.0);
            slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        }
    }

    private static bool IsInsideThumb(DependencyObject? source) =>
        TreeNavigationHelper.FindAncestor<Thumb>(source) != null;

    /// <summary>
    /// Membuka ContextMenu milik ancestor ListBoxItem saat tombol aksi cepat diklik kiri.
    /// </summary>
    public static void OpenAncestorContextMenu(DependencyObject? origin)
    {
        var item = TreeNavigationHelper.FindAncestor<ListBoxItem>(origin);
        if (item != null && item.ContextMenu != null)
        {
            item.IsSelected = true;
            item.Focus();
            item.ContextMenu.PlacementTarget = item;
            item.ContextMenu.Placement = PlacementMode.MousePoint;
            item.ContextMenu.IsOpen = true;
        }
    }
}
