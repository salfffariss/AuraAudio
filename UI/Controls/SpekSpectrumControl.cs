using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.Controls;

/// <summary>
/// v2.0.0 Pure-Monochrome Hardware-Accelerated FFT Nyquist Spectrum Control (03_design_spec.md Part D.3).
/// All brushes are resolved from NeutralTokens.xaml (strictly R = G = B, zero hue).
/// Reference markers are distinguished by dash geometry, stroke weight, luminance tier, and typography.
/// </summary>
public class SpekSpectrumControl : FrameworkElement
{
    public static readonly DependencyProperty ReportProperty =
        DependencyProperty.Register(
            nameof(Report),
            typeof(AudioIntegrityReport),
            typeof(SpekSpectrumControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public AudioIntegrityReport? Report
    {
        get => (AudioIntegrityReport?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    private static readonly Typeface AxisTypeface = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Typeface LabelTypeface = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Typeface BoldLabelTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly DashStyle GridDash = new(new[] { 3.0, 3.0 }, 0);
    private static readonly DashStyle CdLimitDash = new(new[] { 3.0, 3.0 }, 0);
    private static readonly DashStyle CutoffDash = new(new[] { 8.0, 3.0 }, 0);
    private static readonly DashStyle DitherDash = new(new[] { 6.0, 2.0, 2.0, 2.0 }, 0);

    static SpekSpectrumControl()
    {
        GridDash.Freeze();
        CdLimitDash.Freeze();
        CutoffDash.Freeze();
        DitherDash.Freeze();
    }

    public SpekSpectrumControl()
    {
        Loaded += (_, _) => LocalizationManager.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationManager.LanguageChanged -= OnLanguageChanged;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpekSpectrumAutomationPeer(this);

    private sealed class SpekSpectrumAutomationPeer : FrameworkElementAutomationPeer
    {
        public SpekSpectrumAutomationPeer(SpekSpectrumControl owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetClassNameCore() => nameof(SpekSpectrumControl);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(InvalidateVisual);

    private static ResourceDictionary? _fallbackTokens;

    private static ResourceDictionary? GetFallbackTokens()
    {
        if (_fallbackTokens != null) return _fallbackTokens;
        try
        {
            _fallbackTokens = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/AuraAudio;component/UI/Tokens/NeutralTokens.xaml", UriKind.Absolute)
            };
        }
        catch
        {
            // Headless unit test without WPF pack URI application registered
        }
        return _fallbackTokens;
    }

    private Brush ResolveBrush(string resourceKey)
    {
        if (TryFindResource(resourceKey) is Brush localBrush)
        {
            return localBrush;
        }
        if (GetFallbackTokens() is ResourceDictionary tokens && tokens[resourceKey] is Brush tokenBrush)
        {
            return tokenBrush;
        }
        return Brushes.Transparent;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 80 || height < 60) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool isEn = string.Equals(LocalizationManager.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

        Brush bgBrush = ResolveBrush("Brush.SpectrumCanvas");
        Brush borderBrush = ResolveBrush("Brush.Border.Hairline");
        Brush gridBrush = ResolveBrush("Brush.Spectrum.GridLine");
        Brush axisTextBrush = ResolveBrush("Brush.Text.Tertiary");
        Brush cdMarkerBrush = ResolveBrush("Brush.Neutral.65");
        Brush ditherMarkerBrush = ResolveBrush("Brush.Neutral.80");
        Brush peakBrush = ResolveBrush("Brush.Neutral.100");
        Brush fillBrush = ResolveBrush("Brush.Spectrum.Fill");

        var borderPen = new Pen(borderBrush, 1.0);
        var gridPen = new Pen(gridBrush, 0.8) { DashStyle = GridDash };
        var cdPen = new Pen(cdMarkerBrush, 1.2) { DashStyle = CdLimitDash };
        var cutoffPen = new Pen(peakBrush, 1.6) { DashStyle = CutoffDash };
        var ditherPen = new Pen(ditherMarkerBrush, 1.2) { DashStyle = DitherDash };
        var curvePen = new Pen(peakBrush, 2.0);

        double leftMargin = 54.0;
        double rightMargin = 20.0;
        double topMargin = 20.0;
        double bottomMargin = 28.0;

        double plotWidth = Math.Max(10.0, width - leftMargin - rightMargin);
        double plotHeight = Math.Max(10.0, height - topMargin - bottomMargin);
        Rect plotRect = new(leftMargin, topMargin, plotWidth, plotHeight);

        dc.DrawRoundedRectangle(bgBrush, borderPen, plotRect, 8, 8);

        if (Report?.SpectrumDbBins == null || Report.SpectrumDbBins.Length == 0)
        {
            string emptyMsg = isEn
                ? "Select a track or open an audio file to inspect its Nyquist frequency spectrum."
                : "Pilih lagu atau buka berkas audio untuk melihat grafik spektrum frekuensi Nyquist.";
            var promptText = new FormattedText(
                emptyMsg,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                12,
                axisTextBrush,
                dpi);

            dc.DrawText(promptText, new Point(
                leftMargin + (plotWidth - promptText.Width) / 2,
                topMargin + (plotHeight - promptText.Height) / 2));
            return;
        }

        double maxFreqHz = Math.Max(24_000.0, Report.NyquistLimitHz);
        if (maxFreqHz > 96_000.0) maxFreqHz = 96_000.0;

        const double maxDb = 0.0;
        const double minDb = -145.0;
        const double dbRange = maxDb - minDb;

        // Horizontal dBFS grid lines every 20 dB
        for (double db = 0; db >= -140; db -= 20)
        {
            double y = topMargin + ((maxDb - db) / dbRange) * plotHeight;
            dc.DrawLine(gridPen, new Point(leftMargin, y), new Point(leftMargin + plotWidth, y));

            var dbLabel = new FormattedText(
                $"{db:F0} dB",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                9.5,
                axisTextBrush,
                dpi);

            dc.DrawText(dbLabel, new Point(leftMargin - dbLabel.Width - 8, y - dbLabel.Height / 2));
        }

        // Vertical frequency grid lines
        double freqStep = maxFreqHz > 50_000 ? 20_000.0 : (maxFreqHz > 25_000 ? 10_000.0 : 5_000.0);
        for (double f = freqStep; f < maxFreqHz; f += freqStep)
        {
            double x = leftMargin + (f / maxFreqHz) * plotWidth;
            dc.DrawLine(gridPen, new Point(x, topMargin), new Point(x, topMargin + plotHeight));

            var fLabel = new FormattedText(
                $"{f / 1000.0:F0} kHz",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                9.5,
                axisTextBrush,
                dpi);

            dc.DrawText(fLabel, new Point(x - fLabel.Width / 2, topMargin + plotHeight + 6));
        }

        // Reference Marker 1: CD Limit (22.05 kHz) — Neutral.65, 1.2px, short dash {3, 3}
        if (maxFreqHz > 22_050.0)
        {
            double cdX = leftMargin + (22_050.0 / maxFreqHz) * plotWidth;
            dc.DrawLine(cdPen, new Point(cdX, topMargin), new Point(cdX, topMargin + plotHeight));

            string cdText = isEn ? "CD Limit (22.05 kHz)" : "Batas CD (22.05 kHz)";
            var cdLabel = new FormattedText(
                cdText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                9.5,
                cdMarkerBrush,
                dpi);

            dc.DrawText(cdLabel, new Point(Math.Min(cdX + 6, width - rightMargin - cdLabel.Width - 6), topMargin + 6));
        }

        // Reference Marker 2: Measured Cutoff — Neutral.100, 1.6px, long dash {8, 3} + Bold label
        if (Report.DetectedCutoffHz > 0 &&
            Report.DetectedCutoffHz < Report.NyquistLimitHz - 250 &&
            Report.DetectedCutoffHz <= maxFreqHz)
        {
            double cutoffX = leftMargin + (Report.DetectedCutoffHz / maxFreqHz) * plotWidth;
            dc.DrawLine(cutoffPen, new Point(cutoffX, topMargin + 20), new Point(cutoffX, topMargin + plotHeight));

            string cutPrefix = isEn ? "Cutoff" : "Titik Potong";
            var cutoffLabel = new FormattedText(
                $"{cutPrefix}: {Report.DetectedCutoffHz / 1000.0:F1} kHz",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                BoldLabelTypeface,
                9.5,
                peakBrush,
                dpi);

            double labelX = cutoffX - cutoffLabel.Width - 6;
            if (labelX < leftMargin + 4) labelX = cutoffX + 6;
            dc.DrawText(cutoffLabel, new Point(labelX, topMargin + 22));
        }

        // Reference Marker 3: 16-Bit Dither Floor — Neutral.80, 1.2px, dash-dot {6, 2, 2, 2}
        if (Report.IsDitherDetected)
        {
            double ditherDb = Math.Clamp(Report.UltrasonicNoiseFloorDbFS ?? -130.7, minDb + 4.0, maxDb - 10.0);
            double yDither = topMargin + ((maxDb - ditherDb) / dbRange) * plotHeight;
            dc.DrawLine(ditherPen, new Point(leftMargin, yDither), new Point(leftMargin + plotWidth, yDither));

            string ditherText = isEn
                ? $"16-Bit Dither Floor ({ditherDb:F1} dBFS)"
                : $"Lantai Dither 16-Bit ({ditherDb:F1} dBFS)";
            var ditherLabel = new FormattedText(
                ditherText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                9.5,
                ditherMarkerBrush,
                dpi);

            dc.DrawText(ditherLabel, new Point(leftMargin + plotWidth - ditherLabel.Width - 8, yDither - ditherLabel.Height - 4));
        }

        // FFT Power Curve (2.0px Neutral.100 stroke + vertical monochrome luminance fill gradient)
        var bins = Report.SpectrumDbBins;
        int binCount = bins.Length;
        double binResHz = Report.BinResolutionHz > 0 ? Report.BinResolutionHz : (maxFreqHz / binCount);

        var streamGeometry = new StreamGeometry();
        using (var ctx = streamGeometry.Open())
        {
            double startX = leftMargin;
            double startY = topMargin + plotHeight;
            ctx.BeginFigure(new Point(startX, startY), isFilled: true, isClosed: true);

            double lastX = leftMargin;
            int step = Math.Max(1, binCount / 400);

            for (int i = 0; i < binCount; i += step)
            {
                double freq = i * binResHz;
                if (freq > maxFreqHz) break;

                double db = Math.Clamp(bins[i], minDb, maxDb);
                double x = leftMargin + (freq / maxFreqHz) * plotWidth;
                double y = topMargin + ((maxDb - db) / dbRange) * plotHeight;

                ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
                lastX = x;
            }

            ctx.LineTo(new Point(lastX, topMargin + plotHeight), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(leftMargin, topMargin + plotHeight), isStroked: false, isSmoothJoin: false);
        }

        streamGeometry.Freeze();

        dc.PushClip(new RectangleGeometry(plotRect, 8, 8));
        dc.DrawGeometry(fillBrush, curvePen, streamGeometry);
        dc.Pop();
    }
}
