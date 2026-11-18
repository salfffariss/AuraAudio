using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.Views;

/// <summary>
/// Kontrol visualisasi kurva spektrum frekuensi FFT Nyquist yang dirender secara hardware-accelerated.
/// Menampilkan grafik dBFS dari 0 hingga -145 dBFS, batas CD 22.05 kHz, titik potong terukur,
/// serta lantai dither 16-bit (~-130.7 dBFS) bila terdeteksi.
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

    public SpekSpectrumControl()
    {
        Loaded += (_, _) => LocalizationManager.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationManager.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(InvalidateVisual);

    private static readonly Typeface AxisTypeface = new("Cascadia Mono, Consolas, Segoe UI");
    private static readonly Typeface LabelTypeface = new("Segoe UI Variable Text, Segoe UI");
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(148, 144, 135)); // #949087 (AA 5.92:1 on #111114)
    private static readonly SolidColorBrush GridPenBrush = new(Color.FromRgb(40, 40, 50)); // #282832
    private static readonly SolidColorBrush CdMarkerBrush = new(Color.FromRgb(248, 113, 113)); // #F87171
    private static readonly SolidColorBrush CutoffMarkerBrush = new(Color.FromRgb(52, 211, 153)); // #34D399
    private static readonly SolidColorBrush CurveStrokeBrush = new(Color.FromRgb(229, 162, 60)); // #E5A23C Warm Amber
    private static readonly SolidColorBrush DitherMarkerBrush = new(Color.FromRgb(251, 191, 36)); // #FBBF24
    private static readonly SolidColorBrush BgBrush = new(Color.FromRgb(17, 17, 20)); // #111114
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(40, 40, 50)), 1.0);
    private static readonly Pen GridPen = new(GridPenBrush, 0.8) { DashStyle = DashStyles.Dash };
    private static readonly Pen CdPen = new(CdMarkerBrush, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
    private static readonly Pen CutoffPen = new(CutoffMarkerBrush, 1.5) { DashStyle = new DashStyle(new double[] { 6, 2 }, 0) };
    private static readonly Pen DitherPen = new(DitherMarkerBrush, 1.2) { DashStyle = new DashStyle(new double[] { 5, 2 }, 0) };
    private static readonly Pen CurvePen = new(CurveStrokeBrush, 1.8);
    private static readonly LinearGradientBrush FillGradient;

    static SpekSpectrumControl()
    {
        TextBrush.Freeze();
        GridPenBrush.Freeze();
        CdMarkerBrush.Freeze();
        CutoffMarkerBrush.Freeze();
        CurveStrokeBrush.Freeze();
        DitherMarkerBrush.Freeze();
        BgBrush.Freeze();
        BorderPen.Freeze();
        GridPen.Freeze();
        CdPen.Freeze();
        CutoffPen.Freeze();
        DitherPen.Freeze();
        CurvePen.Freeze();

        var gradient = new LinearGradientBrush(
            Color.FromArgb(92, 229, 162, 60),
            Color.FromArgb(8, 17, 17, 20),
            new Point(0, 0),
            new Point(0, 1));
        gradient.Freeze();
        FillGradient = gradient;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 80 || height < 60) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool isEn = string.Equals(LocalizationManager.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

        double leftMargin = 54.0;
        double rightMargin = 20.0;
        double topMargin = 20.0;
        double bottomMargin = 28.0;

        double plotWidth = Math.Max(10.0, width - leftMargin - rightMargin);
        double plotHeight = Math.Max(10.0, height - topMargin - bottomMargin);

        Rect plotRect = new(leftMargin, topMargin, plotWidth, plotHeight);

        dc.DrawRoundedRectangle(BgBrush, BorderPen, plotRect, 6, 6);

        if (Report?.SpectrumDbBins == null || Report.SpectrumDbBins.Length == 0)
        {
            string emptyMsg = isEn
                ? "Select a track or open an audio file to inspect its frequency spectrum."
                : "Pilih lagu atau buka berkas audio untuk melihat grafik spektrum frekuensinya.";
            var promptText = new FormattedText(
                emptyMsg,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                12,
                TextBrush,
                dpi);

            dc.DrawText(promptText, new Point(
                leftMargin + (plotWidth - promptText.Width) / 2,
                topMargin + (plotHeight - promptText.Height) / 2));
            return;
        }

        double maxFreqHz = Math.Max(24_000.0, Report.NyquistLimitHz);
        if (maxFreqHz > 96_000.0) maxFreqHz = 96_000.0;

        // Rentang hingga -145 dBFS agar lantai dither 16-bit (-130.7 dBFS) terlihat utuh
        const double maxDb = 0.0;
        const double minDb = -145.0;
        const double dbRange = maxDb - minDb;

        // Garis horizontal desibel setiap 20 dB
        for (double db = 0; db >= -140; db -= 20)
        {
            double y = topMargin + ((maxDb - db) / dbRange) * plotHeight;
            dc.DrawLine(GridPen, new Point(leftMargin, y), new Point(leftMargin + plotWidth, y));

            var dbLabel = new FormattedText(
                $"{db:F0} dB",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                9.5,
                TextBrush,
                dpi);

            dc.DrawText(dbLabel, new Point(leftMargin - dbLabel.Width - 8, y - dbLabel.Height / 2));
        }

        // Garis vertikal frekuensi
        double freqStep = maxFreqHz > 50_000 ? 20_000.0 : (maxFreqHz > 25_000 ? 10_000.0 : 5_000.0);
        for (double f = freqStep; f < maxFreqHz; f += freqStep)
        {
            double x = leftMargin + (f / maxFreqHz) * plotWidth;
            dc.DrawLine(GridPen, new Point(x, topMargin), new Point(x, topMargin + plotHeight));

            var fLabel = new FormattedText(
                $"{f / 1000.0:F0} kHz",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                9.5,
                TextBrush,
                dpi);

            dc.DrawText(fLabel, new Point(x - fLabel.Width / 2, topMargin + plotHeight + 6));
        }

        // Garis batas Nyquist CD (22.05 kHz)
        if (maxFreqHz > 22_050.0)
        {
            double cdX = leftMargin + (22_050.0 / maxFreqHz) * plotWidth;
            dc.DrawLine(CdPen, new Point(cdX, topMargin), new Point(cdX, topMargin + plotHeight));

            string cdText = isEn ? "CD Limit (22.05 kHz)" : "Batas CD (22.05 kHz)";
            var cdLabel = new FormattedText(
                cdText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                9.5,
                CdMarkerBrush,
                dpi);

            dc.DrawText(cdLabel, new Point(Math.Min(cdX + 6, width - rightMargin - cdLabel.Width - 4), topMargin + 5));
        }

        // Garis batas potong terukur (hanya digambar jika benar-benar memotong di bawah Nyquist file)
        if (Report.DetectedCutoffHz > 0 &&
            Report.DetectedCutoffHz < Report.NyquistLimitHz - 250 &&
            Report.DetectedCutoffHz <= maxFreqHz)
        {
            double cutoffX = leftMargin + (Report.DetectedCutoffHz / maxFreqHz) * plotWidth;
            dc.DrawLine(CutoffPen, new Point(cutoffX, topMargin + 20), new Point(cutoffX, topMargin + plotHeight));

            string cutPrefix = isEn ? "Cutoff" : "Titik Potong";
            var cutoffLabel = new FormattedText(
                $"{cutPrefix}: {Report.DetectedCutoffHz / 1000.0:F1} kHz",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                9.5,
                CutoffMarkerBrush,
                dpi);

            double labelX = cutoffX - cutoffLabel.Width - 6;
            if (labelX < leftMargin + 4) labelX = cutoffX + 6;
            dc.DrawText(cutoffLabel, new Point(labelX, topMargin + 22));
        }

        // Penanda lantai dither 16-bit terukur (~-130.7 dBFS)
        if (Report.IsDitherDetected)
        {
            double ditherDb = Math.Clamp(Report.UltrasonicNoiseFloorDbFS ?? -130.7, minDb + 4.0, maxDb - 10.0);
            double yDither = topMargin + ((maxDb - ditherDb) / dbRange) * plotHeight;
            dc.DrawLine(DitherPen, new Point(leftMargin, yDither), new Point(leftMargin + plotWidth, yDither));

            string ditherText = isEn
                ? $"16-Bit Dither Floor ({ditherDb:F1} dBFS)"
                : $"Lantai Dither 16-Bit ({ditherDb:F1} dBFS)";
            var ditherLabel = new FormattedText(
                ditherText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                9.5,
                DitherMarkerBrush,
                dpi);

            dc.DrawText(ditherLabel, new Point(leftMargin + plotWidth - ditherLabel.Width - 8, yDither - ditherLabel.Height - 3));
        }

        // Gambar kurva spektrum daya FFT
        var bins = Report.SpectrumDbBins;
        int binCount = bins.Length;
        double binResHz = Report.BinResolutionHz > 0 ? Report.BinResolutionHz : (maxFreqHz * 2.0 / (binCount * 2));

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

        dc.PushClip(new RectangleGeometry(plotRect, 6, 6));
        dc.DrawGeometry(FillGradient, CurvePen, streamGeometry);
        dc.Pop();
    }
}
