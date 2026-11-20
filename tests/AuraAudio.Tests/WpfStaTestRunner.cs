using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace AuraAudio.Tests;

[CollectionDefinition("WpfSta", DisableParallelization = true)]
public class WpfStaCollectionDefinition
{
}

/// <summary>
/// Single persistent WPF STA dispatcher thread shared across all UI tests so
/// Application.Current, merged neutral token dictionaries, and BAML PackagePart
/// streams are always accessed sequentially on their owning UI thread.
/// </summary>
internal static class WpfStaTestRunner
{
    private static readonly object InitLock = new();
    private static Dispatcher? _dispatcher;
    private static bool _resourcesLoaded;

    public static void Run(Action action)
    {
        EnsureDispatcher();

        Exception? captured = null;
        _dispatcher!.Invoke(() =>
        {
            try
            {
                EnsureAppResourcesLoaded();
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        if (captured != null)
        {
            throw new Exception("STA test failed: " + captured.Message, captured);
        }
    }

    private static void EnsureDispatcher()
    {
        if (_dispatcher != null) return;

        lock (InitLock)
        {
            if (_dispatcher != null) return;

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "AuraAudio.WpfStaTestThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
        }
    }

    private static void EnsureAppResourcesLoaded()
    {
        if (_resourcesLoaded && Application.Current != null) return;

        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            string[] dicts =
            [
                "/AuraAudio;component/UI/Tokens/NeutralTokens.xaml",
                "/AuraAudio;component/UI/Tokens/SystemOverrides.xaml",
                "/AuraAudio;component/UI/Tokens/Metrics.xaml",
                "/AuraAudio;component/UI/Tokens/Typography.xaml",
                "/AuraAudio;component/UI/Tokens/Motion.xaml",
                "/AuraAudio;component/UI/Styles/Controls.xaml",
                "/AuraAudio;component/UI/Resources/Strings.en.xaml"
            ];
            foreach (string uri in dicts)
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(uri, UriKind.Relative)
                });
            }

            app.Resources["TimeSpanToStringConverter"] = new AuraAudio.UI.Helpers.TimeSpanToStringConverter();
            app.Resources["BooleanToVisibilityConverter"] = new AuraAudio.UI.Helpers.BooleanToVisibilityConverter();
            app.Resources["NullToVisibilityConverter"] = new AuraAudio.UI.Helpers.NullToVisibilityConverter();
            app.Resources["StringMatchToVisibilityConverter"] = new AuraAudio.UI.Helpers.StringMatchToVisibilityConverter();
            app.Resources["StringMatchToBoolConverter"] = new AuraAudio.UI.Helpers.StringMatchToBoolConverter();
            app.Resources["InvertBoolConverter"] = new AuraAudio.UI.Helpers.InvertBoolConverter();
            app.Resources["BooleanToStringConverter"] = new AuraAudio.UI.Helpers.BooleanToStringConverter();
            app.Resources["NumberToVisibilityConverter"] = new AuraAudio.UI.Helpers.NumberToVisibilityConverter();
            app.Resources["OneBasedIndexConverter"] = new AuraAudio.UI.Helpers.OneBasedIndexConverter();
            app.Resources["StringEqualsMultiConverter"] = new AuraAudio.UI.Helpers.StringEqualsMultiConverter();
            app.Resources["SortIndicatorVisibilityConverter"] = new AuraAudio.UI.Helpers.SortIndicatorVisibilityConverter();
            app.Resources["ActiveTrackPlayingMultiConverter"] = new AuraAudio.UI.Helpers.ActiveTrackPlayingMultiConverter();
            app.Resources["CoverArtConverter"] = new AuraAudio.UI.Helpers.CoverArtConverter();
        }

        _resourcesLoaded = true;
    }
}
