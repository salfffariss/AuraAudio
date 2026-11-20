using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.UI.Controls;
using AuraAudio.UI.Helpers;
using Xunit;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class Stage6MotionTests
{
    [Fact]
    public void SpringAnimator_ReducedMotionOverride_FiresEventAndAvoidsSpatialTransformMotion()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool? originalOverride = SpringAnimator.ForceReducedMotionOverride;
            int eventCount = 0;
            void Handler() => eventCount++;

            SpringAnimator.ReducedMotionChanged += Handler;
            try
            {
                SpringAnimator.ForceReducedMotionOverride = true;
                Assert.True(SpringAnimator.IsReducedMotion);
                Assert.Equal(1, eventCount);

                var card = new Border { Width = 300, Height = 200 };
                var (scale, translate) = SpringAnimator.EnsureTransformGroup(card);

                SpringAnimator.AnimateSpatialExpand(
                    card,
                    origin: new Point(0.5, 0.9),
                    fromScale: 0.94,
                    fromY: 22.0,
                    dampingRatio: 1.0,
                    response: 0.34);

                // Under Reduced Motion, vestibular transforms stay fixed at identity while opacity cross-fades
                Assert.Equal(1.0, scale.ScaleX);
                Assert.Equal(1.0, scale.ScaleY);
                Assert.Equal(0.0, translate.Y);

                SpringAnimator.AnimateSpatialCollapse(
                    card,
                    origin: new Point(0.5, 0.9),
                    toScale: 0.95,
                    toY: 18.0,
                    dampingRatio: 1.0,
                    response: 0.22);

                Assert.Equal(1.0, scale.ScaleX);
                Assert.Equal(1.0, scale.ScaleY);
                Assert.Equal(0.0, translate.Y);

                SpringAnimator.Cancel(card, "opacity");
            }
            finally
            {
                SpringAnimator.ReducedMotionChanged -= Handler;
                SpringAnimator.ForceReducedMotionOverride = originalOverride;
            }
        });
    }

    [Fact]
    public void FluidBehaviors_ElasticScrubberExpandsOnFocusAndSkipsPressScaleUnderReducedMotion()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool? originalOverride = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.ForceReducedMotionOverride = true;

            var slider = new Slider
            {
                Width = 240,
                Minimum = 0,
                Maximum = 100,
                Value = 40,
                Style = (Style)Application.Current.FindResource("Style.Slider.MonochromeScrubber")
            };
            FluidBehaviors.SetEnableDirectScrub(slider, true);

            var btn = new Button
            {
                Content = "Play",
                Style = (Style)Application.Current.FindResource("Style.Button.Primary")
            };

            var panel = new StackPanel();
            panel.Children.Add(slider);
            panel.Children.Add(btn);

            var host = new Window
            {
                Width = 400,
                Height = 200,
                Content = panel,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                var trackBg = Assert.IsType<Border>(slider.Template.FindName("ElasticTrackBackground", slider));
                Assert.Equal(3.0, trackBg.Height);

                // Focus scrubber -> expands cleanly to 6px
                slider.Focus();
                FluidBehaviors.UpdateElasticScrubberVisuals(slider);
                Assert.Equal(6.0, trackBg.Height);

                // Clear focus -> collapses cleanly back to 3px
                Keyboard.ClearFocus();
                FluidBehaviors.UpdateElasticScrubberVisuals(slider);
                Assert.Equal(3.0, trackBg.Height);

                // Under Reduced Motion, pointer-down press feedback must NOT apply scale jump
                var (scale, _) = SpringAnimator.EnsureTransformGroup(btn);
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = btn
                };
                btn.RaiseEvent(down);
                Assert.Equal(1.0, scale.ScaleX);
            }
            finally
            {
                host.Close();
                SpringAnimator.ForceReducedMotionOverride = originalOverride;
            }
        });
    }

    [Fact]
    public void BouncyBarControl_PausesAndResetsRestingBarsWhenReducedMotionEnabled()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool? originalOverride = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.ForceReducedMotionOverride = false;

            var bar = new BouncyBarControl { IsActive = true };
            var host = new Window
            {
                Width = 120,
                Height = 120,
                Content = bar,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                // Toggle Reduced Motion ON live -> bars immediately pause at resting heights (6, 12, 8, 10)
                SpringAnimator.ForceReducedMotionOverride = true;
                host.UpdateLayout();

                var b1 = Assert.IsType<Border>(bar.FindName("Bar1"));
                var b2 = Assert.IsType<Border>(bar.FindName("Bar2"));
                var b3 = Assert.IsType<Border>(bar.FindName("Bar3"));
                var b4 = Assert.IsType<Border>(bar.FindName("Bar4"));
                Assert.Equal(6.0, b1.Height);
                Assert.Equal(12.0, b2.Height);
                Assert.Equal(8.0, b3.Height);
                Assert.Equal(10.0, b4.Height);
            }
            finally
            {
                host.Close();
                SpringAnimator.ForceReducedMotionOverride = originalOverride;
            }
        });
    }
}
