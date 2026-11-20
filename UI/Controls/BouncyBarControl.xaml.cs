using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.Controls;

/// <summary>
/// Pure-monochrome 4-bar active track equalizer (Apple Music BouncyBarControl equivalent).
/// Uses zero per-frame allocations and pauses automatically when hidden, inactive, or under Reduced Motion.
/// </summary>
public partial class BouncyBarControl : UserControl
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(BouncyBarControl),
            new PropertyMetadata(false, OnStateChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private double _phase;

    public BouncyBarControl()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(55)
        };
        _timer.Tick += OnTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => SyncTimer();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SpringAnimator.ReducedMotionChanged += OnReducedMotionChanged;
        SyncTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SpringAnimator.ReducedMotionChanged -= OnReducedMotionChanged;
        _timer.Stop();
    }

    private void OnReducedMotionChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            SyncTimer();
        }
        else
        {
            Dispatcher.BeginInvoke(SyncTimer);
        }
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BouncyBarControl ctrl)
        {
            ctrl.SyncTimer();
        }
    }

    private void SyncTimer()
    {
        if (IsLoaded && IsVisible && IsActive && !SpringAnimator.IsReducedMotion)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
            Bar1.Height = 6;
            Bar2.Height = 12;
            Bar3.Height = 8;
            Bar4.Height = 10;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _phase += 0.36;
        Bar1.Height = 3.5 + 9.5 * (0.5 + 0.5 * Math.Sin(_phase));
        Bar2.Height = 3.5 + 10.5 * (0.5 + 0.5 * Math.Sin(_phase * 1.35 + 1.1));
        Bar3.Height = 3.5 + 9.5 * (0.5 + 0.5 * Math.Sin(_phase * 1.15 + 2.4));
        Bar4.Height = 3.5 + 8.5 * (0.5 + 0.5 * Math.Sin(_phase * 1.50 + 3.8));
    }
}
