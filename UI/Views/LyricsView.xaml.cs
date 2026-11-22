using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AuraAudio.UI.Controls;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.1 Lyrics View shared across Full-Screen Split (IsFullScreenMode=True),
/// 360px Right Split Pane (IsFullScreenMode=False), and Mini-Player Tray (IsFullScreenMode=False).
/// Delegates synced rendering to the zero-layout pooled SyncedLyricsViewport while preserving
/// all 5 LyricsViewState states, plain-text fallback, keyboard navigation, and unobtrusive Import.
/// </summary>
public partial class LyricsView : UserControl
{
    public static readonly DependencyProperty IsFullScreenModeProperty =
        DependencyProperty.Register(
            nameof(IsFullScreenMode),
            typeof(bool),
            typeof(LyricsView),
            new PropertyMetadata(false, OnFullScreenModeChanged));

    public static readonly DependencyProperty PlainFontSizeProperty =
        DependencyProperty.Register(nameof(PlainFontSize), typeof(double), typeof(LyricsView), new PropertyMetadata(15.0));

    public static readonly DependencyProperty PlainLineHeightProperty =
        DependencyProperty.Register(nameof(PlainLineHeight), typeof(double), typeof(LyricsView), new PropertyMetadata(24.0));

    public static readonly DependencyProperty PlainFontWeightProperty =
        DependencyProperty.Register(nameof(PlainFontWeight), typeof(FontWeight), typeof(LyricsView), new PropertyMetadata(FontWeights.Normal));

    public static readonly DependencyProperty PlainFontFamilyProperty =
        DependencyProperty.Register(nameof(PlainFontFamily), typeof(FontFamily), typeof(LyricsView), new PropertyMetadata(null));

    public static readonly DependencyProperty PlainTextPaddingProperty =
        DependencyProperty.Register(nameof(PlainTextPadding), typeof(Thickness), typeof(LyricsView), new PropertyMetadata(new Thickness(10, 16, 12, 48)));

    public static readonly DependencyProperty EdgeMaskBrushProperty =
        DependencyProperty.Register(nameof(EdgeMaskBrush), typeof(Brush), typeof(LyricsView), new PropertyMetadata(null));

    public bool IsFullScreenMode
    {
        get => (bool)GetValue(IsFullScreenModeProperty);
        set => SetValue(IsFullScreenModeProperty, value);
    }

    public double PlainFontSize
    {
        get => (double)GetValue(PlainFontSizeProperty);
        private set => SetValue(PlainFontSizeProperty, value);
    }

    public double PlainLineHeight
    {
        get => (double)GetValue(PlainLineHeightProperty);
        private set => SetValue(PlainLineHeightProperty, value);
    }

    public FontWeight PlainFontWeight
    {
        get => (FontWeight)GetValue(PlainFontWeightProperty);
        private set => SetValue(PlainFontWeightProperty, value);
    }

    public FontFamily? PlainFontFamily
    {
        get => (FontFamily?)GetValue(PlainFontFamilyProperty);
        private set => SetValue(PlainFontFamilyProperty, value);
    }

    public Thickness PlainTextPadding
    {
        get => (Thickness)GetValue(PlainTextPaddingProperty);
        private set => SetValue(PlainTextPaddingProperty, value);
    }

    public Brush? EdgeMaskBrush
    {
        get => (Brush?)GetValue(EdgeMaskBrushProperty);
        private set => SetValue(EdgeMaskBrushProperty, value);
    }

    internal SyncedLyricsViewport SyncedViewportControl => SyncedViewport;

    private LyricsViewModel? _subscribedLyrics;

    public LyricsView()
    {
        InitializeComponent();
        ApplyModeTokens(false);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnFullScreenModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsView view && e.NewValue is bool fs)
        {
            view.ApplyModeTokens(fs);
        }
    }

    private void ApplyModeTokens(bool isFullScreen)
    {
        SyncedViewport.IsFullScreenMode = isFullScreen;

        if (isFullScreen)
        {
            PlainFontSize = 20.0;
            PlainLineHeight = 32.0;
            PlainFontWeight = FontWeights.SemiBold;
            PlainFontFamily = TryFindResource("Font.Display") as FontFamily;
            PlainTextPadding = new Thickness(12, 24, 16, 56);
            EdgeMaskBrush = TryFindResource("Brush.Overlay.LyricsEdgeMask") as Brush;
        }
        else
        {
            PlainFontSize = 15.0;
            PlainLineHeight = 24.0;
            PlainFontWeight = FontWeights.Normal;
            PlainFontFamily = TryFindResource("Font.Primary") as FontFamily;
            PlainTextPadding = new Thickness(10, 16, 12, 48);
            EdgeMaskBrush = TryFindResource("Brush.Overlay.LyricsEdgeMaskCompact") as Brush;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyModeTokens(IsFullScreenMode);
        AttachToViewModel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachFromViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachToViewModel();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _subscribedLyrics != null)
        {
            SyncedViewport.SyncToActiveLine(immediate: true);
        }
    }

    private void AttachToViewModel()
    {
        DetachFromViewModel();
        if (DataContext is MainViewModel vm)
        {
            _subscribedLyrics = vm.Lyrics;
            _subscribedLyrics.PropertyChanged += OnLyricsPropertyChanged;
            SyncedViewport.AttachLyrics(_subscribedLyrics);
        }
        else if (DataContext is LyricsViewModel directLyrics)
        {
            _subscribedLyrics = directLyrics;
            _subscribedLyrics.PropertyChanged += OnLyricsPropertyChanged;
            SyncedViewport.AttachLyrics(_subscribedLyrics);
        }
    }

    private void DetachFromViewModel()
    {
        SyncedViewport.DetachLyrics();
        if (_subscribedLyrics != null)
        {
            _subscribedLyrics.PropertyChanged -= OnLyricsPropertyChanged;
            _subscribedLyrics = null;
        }
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.State) && _subscribedLyrics?.IsStaticLoaded == true)
        {
            PlainLyricsScroll.ScrollToTop();
        }
    }
}
