using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace AuraAudio.UI.Views;

/// <summary>
/// Mini animated waveform visualizer reacting to playback state.
/// Runs smooth WPF hardware-accelerated animations when playing.
/// </summary>
public partial class MiniWaveformVisualizer : UserControl
{
    private Storyboard? _storyboard;

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(MiniWaveformVisualizer),
            new PropertyMetadata(false, OnIsPlayingChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public MiniWaveformVisualizer()
    {
        InitializeComponent();
        Loaded += MiniWaveformVisualizer_Loaded;
    }

    private void MiniWaveformVisualizer_Loaded(object sender, RoutedEventArgs e)
    {
        _storyboard = CreateStoryboard();
        UpdateAnimationState();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MiniWaveformVisualizer visualizer)
        {
            visualizer.UpdateAnimationState();
        }
    }

    private void UpdateAnimationState()
    {
        if (_storyboard == null) return;

        if (IsPlaying && SystemParameters.ClientAreaAnimation)
        {
            _storyboard.Begin(this, true);
        }
        else
        {
            _storyboard.Stop(this);
            // Reset bar heights to default resting state
            Bar1.Height = 4;
            Bar2.Height = 8;
            Bar3.Height = 5;
            Bar4.Height = 10;
        }
    }

    private Storyboard CreateStoryboard()
    {
        var sb = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };

        var anim1 = new DoubleAnimation(3, 14, TimeSpan.FromMilliseconds(380));
        Storyboard.SetTarget(anim1, Bar1);
        Storyboard.SetTargetProperty(anim1, new PropertyPath("Height"));

        var anim2 = new DoubleAnimation(6, 16, TimeSpan.FromMilliseconds(520));
        Storyboard.SetTarget(anim2, Bar2);
        Storyboard.SetTargetProperty(anim2, new PropertyPath("Height"));

        var anim3 = new DoubleAnimation(4, 12, TimeSpan.FromMilliseconds(320));
        Storyboard.SetTarget(anim3, Bar3);
        Storyboard.SetTargetProperty(anim3, new PropertyPath("Height"));

        var anim4 = new DoubleAnimation(5, 15, TimeSpan.FromMilliseconds(460));
        Storyboard.SetTarget(anim4, Bar4);
        Storyboard.SetTargetProperty(anim4, new PropertyPath("Height"));

        sb.Children.Add(anim1);
        sb.Children.Add(anim2);
        sb.Children.Add(anim3);
        sb.Children.Add(anim4);

        return sb;
    }
}
