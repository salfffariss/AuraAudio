using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AuraAudio.Core.Models;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Windows;
using Xunit;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class VisualTreeClickSafetyTests
{
    private sealed class NonVisualDependencyObject : DependencyObject { }

    [Fact]
    public void TreeNavigationHelper_HandlesContentElementsAndNonVisualsWithoutThrowing()
    {
        WpfStaTestRunner.Run(() =>
        {
            var run = new Run("Track Artist");
            var italic = new Italic(run);
            var hyperlink = new Hyperlink(new Run("Open Link"));
            var bold = new Bold(new Run("Bold Title"));
            var lineBreak = new LineBreak();
            var textBlock = new TextBlock();
            textBlock.Inlines.Add(italic);
            textBlock.Inlines.Add(hyperlink);
            textBlock.Inlines.Add(bold);
            textBlock.Inlines.Add(lineBreak);

            var button = new Button { Content = textBlock };
            var host = new Window
            {
                Width = 300,
                Height = 200,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000,
                Content = button
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                Assert.Same(button, TreeNavigationHelper.FindAncestor<ButtonBase>(run));
                Assert.Same(button, TreeNavigationHelper.FindAncestor<ButtonBase>(italic));
                Assert.Same(button, TreeNavigationHelper.FindAncestor<ButtonBase>(hyperlink));
                Assert.Same(button, TreeNavigationHelper.FindAncestor<ButtonBase>(hyperlink.Inlines.FirstInline));
                Assert.Same(button, TreeNavigationHelper.FindAncestor<ButtonBase>(bold));
                Assert.Same(button, TreeNavigationHelper.FindAncestor<ButtonBase>(lineBreak));
                Assert.Same(textBlock, TreeNavigationHelper.FindAncestor<TextBlock>(run));

                // Detached non-visual DependencyObject returns null cleanly without throwing
                var nonVisual = new NonVisualDependencyObject();
                Assert.Null(TreeNavigationHelper.GetParent(nonVisual));
                Assert.Null(TreeNavigationHelper.FindAncestor<ButtonBase>(nonVisual));
                Assert.False(TreeNavigationHelper.HasAncestor(nonVisual, static _ => false));
                Assert.Null(TreeNavigationHelper.GetParent(null));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void PreviewMouseLeftButtonDown_OnRunHyperlinkSpanBoldInsideMainWindow_DoesNotThrow()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool? prevReduced = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.ForceReducedMotionOverride = true;
            MainWindow? win = null;
            try
            {
                win = new MainWindow
                {
                    Width = 1280,
                    Height = 800,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000
                };
                win.Show();
                Pump(120);

                var run = new Run("Artist Name");
                var hyperlink = new Hyperlink(new Run("Link Text"));
                var bold = new Bold(new Run("Bold Text"));
                var span = new Span(new Italic(new Run("Italic Span Text")));
                var lineBreak = new LineBreak();
                var hostBlock = new TextBlock();
                hostBlock.Inlines.Add(run);
                hostBlock.Inlines.Add(hyperlink);
                hostBlock.Inlines.Add(bold);
                hostBlock.Inlines.Add(span);
                hostBlock.Inlines.Add(lineBreak);

                if (win.Content is Panel rootPanel)
                {
                    rootPanel.Children.Add(hostBlock);
                    win.UpdateLayout();
                }

                foreach (IInputElement inlineSource in new IInputElement[]
                {
                    run,
                    hyperlink,
                    hyperlink.Inlines.FirstInline!,
                    bold,
                    bold.Inlines.FirstInline!,
                    span,
                    span.Inlines.FirstInline!,
                    lineBreak
                })
                {
                    var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                        Source = inlineSource
                    };
                    win.RaiseEvent(args);
                }
            }
            finally
            {
                SpringAnimator.ForceReducedMotionOverride = prevReduced;
                try { win?.Close(); } catch { }
            }
        });
    }

    [Fact]
    public void Smoke_ClickingTextAndInlineRunsInInspectorSettingsInfoSheetAndLyrics_DoesNotCrash()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool? prevReduced = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.ForceReducedMotionOverride = true;
            MainWindow? win = null;
            try
            {
                win = new MainWindow
                {
                    Width = 1360,
                    Height = 860,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000
                };
                win.Show();
                Pump(150);

                var vm = Assert.IsType<MainViewModel>(win.DataContext);
                var spec = new AudioStreamSpec(AudioCodec.FLAC, 96_000, BitDepth.Bit24, 2, 2_400);
                var track = new AuraAudio.Core.Models.Track(
                    id: 777_001,
                    filePath: "nonexistent_click_smoke.flac",
                    title: "Click Safety Track",
                    artist: "Click Artist",
                    sortArtist: "Click Artist",
                    album: "Click Album",
                    albumArtist: "Click Artist",
                    year: 2026,
                    trackNumber: 1,
                    discNumber: 1,
                    duration: TimeSpan.FromMinutes(3),
                    streamSpec: spec,
                    fileSizeBytes: 54_000_000,
                    dateAdded: DateTime.UtcNow);

                var report = new AudioIntegrityReport(
                    sourceSpec: spec,
                    hardwareSpec: spec,
                    verdict: AuthenticityVerdict.VerifiedLossless,
                    detectedCutoffHz: 48_000,
                    explanation: "Full bandwidth verified up to 48.0 kHz with genuine 24-bit studio dynamic range.",
                    isTrue24Bit: true,
                    lsbActivityPercentage: 98.5,
                    spectrumDbBins: new double[256]);

                // 1. Inspector page (includes <Run> inside Artist · Album header + explanation TextBlock)
                vm.CurrentView = "Inspector";
                vm.InspectedTrack = track;
                vm.CurrentIntegrityReport = report;
                Pump(140);
                int inspectorClicks = ClickAllTextBlocksAndRuns(win);
                Assert.True(inspectorClicks > 0, "Expected live TextBlock/Run elements on Inspector view.");

                // 2. Settings page
                vm.CurrentView = "Settings";
                Pump(140);
                int settingsClicks = ClickAllTextBlocksAndRuns(win);
                Assert.True(settingsClicks > 0, "Expected live TextBlock/Run elements on Settings view.");

                // 3. Info Sheet (Audio & Path + Authenticity tabs)
                vm.PrepareInfoSheetForTrack(track);
                vm.CurrentIntegrityReport = report;
                vm.Presentation.ActiveInfoTab = "Audio";
                vm.Presentation.IsInfoSheetOpen = true;
                Pump(140);
                int infoClicks = ClickAllTextBlocksAndRuns(win);
                Assert.True(infoClicks > 0, "Expected live TextBlock/Run elements in Info sheet.");
                vm.Presentation.CloseInfoSheetCommand.Execute(null);
                Pump(80);

                // 4. Lyrics pane
                vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics;
                Pump(140);
                int lyricsClicks = ClickAllTextBlocksAndRuns(win);
                Assert.True(lyricsClicks > 0, "Expected live TextBlock/Run elements in Lyrics pane.");
            }
            finally
            {
                SpringAnimator.ForceReducedMotionOverride = prevReduced;
                try { win?.Close(); } catch { }
            }
        });
    }

    private static int ClickAllTextBlocksAndRuns(DependencyObject root)
    {
        var targets = new List<IInputElement>();
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0 && targets.Count < 80)
        {
            var cur = queue.Dequeue();
            if (cur is TextBlock tb && tb.IsVisible)
            {
                targets.Add(tb);
                foreach (var inline in tb.Inlines)
                {
                    targets.Add(inline);
                }
            }

            if (cur is Visual || cur is System.Windows.Media.Media3D.Visual3D)
            {
                int count = VisualTreeHelper.GetChildrenCount(cur);
                for (int i = 0; i < count; i++)
                {
                    queue.Enqueue(VisualTreeHelper.GetChild(cur, i));
                }
            }
        }

        foreach (var target in targets)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = target
            };

            if (target is UIElement ue) ue.RaiseEvent(args);
            else if (target is ContentElement ce) ce.RaiseEvent(args);
        }

        return targets.Count;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
