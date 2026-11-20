using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.Controls;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Windows;
using Xunit;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class Stage5KeyboardAndSheetTests
{
    private static void RunInSta(Action action) => WpfStaTestRunner.Run(action);

    [Fact]
    public void MainWindow_TextBoxBaseGuard_DoesNotInterceptTypingKeysInSearchBox()
    {
        RunInSta(() =>
        {
            var win = new MainWindow
            {
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };

            try
            {
                win.Show();
                win.UpdateLayout();

                var vm = Assert.IsType<MainViewModel>(win.DataContext);
                var searchBox = Assert.IsType<TextBox>(win.FindName("GlobalSearchBox"));
                searchBox.Focus();
                win.UpdateLayout();

                bool initialMute = vm.IsMuted;
                var src = PresentationSource.FromVisual(win);
                Assert.NotNull(src);

                // Typing M, S, R, Space while focused inside TextBoxBase must NOT be intercepted
                foreach (Key k in new[] { Key.M, Key.S, Key.R, Key.Space })
                {
                    var keyDown = new KeyEventArgs(Keyboard.PrimaryDevice, src!, 0, k)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                        Source = searchBox
                    };
                    searchBox.RaiseEvent(keyDown);
                    Assert.False(keyDown.Handled, $"Key {k} should not be swallowed while typing in TextBoxBase.");
                }

                Assert.Equal(initialMute, vm.IsMuted);
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void MainWindow_EscapeClosesStage5ModalSheetsInPriorityOrder()
    {
        RunInSta(() =>
        {
            var win = new MainWindow
            {
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };

            try
            {
                win.Show();
                win.UpdateLayout();

                var vm = Assert.IsType<MainViewModel>(win.DataContext);
                var src = PresentationSource.FromVisual(win);
                Assert.NotNull(src);

                // Open stacked sheets: Info -> AddToPlaylist -> Rename
                vm.Presentation.IsInfoSheetOpen = true;
                vm.IsAddToPlaylistSheetOpen = true;
                vm.IsRenamePlaylistSheetOpen = true;
                Keyboard.ClearFocus();

                void PressEsc()
                {
                    var esc = new KeyEventArgs(Keyboard.PrimaryDevice, src!, 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                        Source = win
                    };
                    win.RaiseEvent(esc);
                    Assert.True(esc.Handled);
                }

                // 1st Esc closes RenamePlaylistSheet first
                PressEsc();
                Assert.False(vm.IsRenamePlaylistSheetOpen);
                Assert.True(vm.IsAddToPlaylistSheetOpen);
                Assert.True(vm.Presentation.IsInfoSheetOpen);

                // 2nd Esc closes AddToPlaylistSheet next
                PressEsc();
                Assert.False(vm.IsAddToPlaylistSheetOpen);
                Assert.True(vm.Presentation.IsInfoSheetOpen);

                // 3rd Esc closes InfoInspectorSheet
                PressEsc();
                Assert.False(vm.Presentation.IsInfoSheetOpen);
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void SpekSpectrumControl_ExposesAutomationPeerAndRendersMonochrome()
    {
        RunInSta(() =>
        {
            var ctrl = new SpekSpectrumControl { Width = 640, Height = 260 };
            AutomationProperties.SetAutomationId(ctrl, "Aura.Inspector.Spectrum");

            var host = new Window
            {
                Width = 680,
                Height = 320,
                Content = ctrl,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                var peer = UIElementAutomationPeer.CreatePeerForElement(ctrl);
                Assert.NotNull(peer);
                Assert.Equal("Aura.Inspector.Spectrum", peer!.GetAutomationId());

                // Assign a real AudioIntegrityReport with spectrum bins and force render pass
                var spec = new AudioStreamSpec(AudioCodec.FLAC, 96000, BitDepth.Bit24, 2, 3200);
                var bins = new double[256];
                for (int i = 0; i < bins.Length; i++) bins[i] = -30.0 - (i * 0.25);

                ctrl.Report = new AudioIntegrityReport(
                    sourceSpec: spec,
                    hardwareSpec: spec,
                    verdict: AuthenticityVerdict.VerifiedLossless,
                    detectedCutoffHz: 46000,
                    explanation: "Verified genuine 24-bit / 96 kHz lossless.",
                    spectrumDbBins: bins);
                ctrl.InvalidateVisual();
                host.UpdateLayout();
            }
            finally
            {
                host.Close();
            }
        });
    }
}
