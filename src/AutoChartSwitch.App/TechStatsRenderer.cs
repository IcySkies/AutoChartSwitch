using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoChartSwitch.Core;

namespace AutoChartSwitch.App;

public sealed class TechStatsRenderer : FrameworkElement
{
    private const double NativeWidth = 124;
    private const double NativeHeight = 46;
    private const double AnimationStepSeconds = 1d / 60d;
    private static readonly BitmapImage SixRowLabels = LoadImage("sp_techstats2025_0_padded.png");
    private static readonly BitmapImage FiveRowLabels = LoadImage("sp_techstats2025_1_padded.png");
    private static readonly BitmapImage[] Digits = Enumerable.Range(0, 10).Select(index => LoadImage($"sp_font_techstat_{index}.png")).ToArray();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _targetValues = new double[6];
    private readonly double[] _animatedValues = new double[6];
    private readonly decimal[] _displayValues = new decimal[6];
    private double _previousSeconds;
    private double _stepAccumulator;
    private bool _hasChart;

    public TechStatsRenderer()
    {
        Width = NativeWidth * 4;
        Height = NativeHeight * 4;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void SetChart(ChartInfo? chart)
    {
        _hasChart = chart is not null;
        if (chart is null)
        {
            Array.Clear(_targetValues, 0, _targetValues.Length);
            Array.Clear(_animatedValues, 0, _animatedValues.Length);
            Array.Clear(_displayValues, 0, _displayValues.Length);
            _stepAccumulator = 0;
            _previousSeconds = _clock.Elapsed.TotalSeconds;
            InvalidateVisual();
            return;
        }

        var values = (chart.TechStats ?? new ChartTechStats()).Values;
        for (var i = 0; i < values.Count; i++)
        {
            _targetValues[i] = (double)values[i];
            _displayValues[i] = values[i];
        }
        _stepAccumulator = 0;
        _previousSeconds = _clock.Elapsed.TotalSeconds;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!_hasChart || ActualWidth <= 0 || ActualHeight <= 0) return;

        var scale = Math.Min(ActualWidth / NativeWidth, ActualHeight / NativeHeight);
        var offsetX = (ActualWidth - (NativeWidth * scale)) / 2;
        var offsetY = (ActualHeight - (NativeHeight * scale)) / 2;
        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));

        var hasGimmick = _targetValues[5] > 0;
        var rowCount = hasGimmick ? 6 : 5;
        drawingContext.DrawImage(hasGimmick ? SixRowLabels : FiveRowLabels, new Rect(4, 3, 36, 40));
        for (var i = 0; i < rowCount; i++)
        {
            var rowTop = 3 + (i * (hasGimmick ? 7 : 8));
            DrawNumber(drawingContext, _displayValues[i], rowTop);
            DrawBar(drawingContext, _animatedValues[i], rowTop);
        }

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _previousSeconds = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => CompositionTarget.Rendering -= OnRendering;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_hasChart) return;
        var now = _clock.Elapsed.TotalSeconds;
        _stepAccumulator = Math.Min(_stepAccumulator + now - _previousSeconds, 0.1d);
        _previousSeconds = now;
        while (_stepAccumulator >= AnimationStepSeconds)
        {
            for (var i = 0; i < _animatedValues.Length; i++)
                _animatedValues[i] += (_targetValues[i] - _animatedValues[i]) * 0.2d;
            _stepAccumulator -= AnimationStepSeconds;
        }
        InvalidateVisual();
    }

    private void DrawBar(DrawingContext drawingContext, double value, double top)
    {
        value = Math.Max(0, value);
        if (value > 200)
        {
            var hue = ((_clock.Elapsed.TotalMilliseconds / 2d) % 255d + 255d) % 255d;
            drawingContext.DrawRectangle(new SolidColorBrush(FromGameMakerHsv(hue, 200, 255)), null, new Rect(42, top, 47, 4));
            drawingContext.DrawRectangle(Brushes.White, null, new Rect(42, top, 47 * Math.Min(value - 200, 200) / 200d, 4));
            return;
        }

        drawingContext.DrawRectangle(new SolidColorBrush(FromGameMakerHsv(55 + value, 200, 255)), null,
            new Rect(42, top, 47 * Math.Min(value, 200) / 200d, 4));
    }

    private static void DrawNumber(DrawingContext drawingContext, decimal value, double top)
    {
        var text = decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        var x = 120 - ((text.Length * 4) + Math.Max(0, text.Length - 1));
        foreach (var character in text)
        {
            drawingContext.DrawImage(Digits[character - '0'], new Rect(x, top, 4, 5));
            x += 5;
        }
    }

    private static BitmapImage LoadImage(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Assets/TechStats/Original/{fileName}", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Color FromGameMakerHsv(double hue, double saturation, double brightness)
    {
        var h = hue / 255d * 360d;
        var s = saturation / 255d;
        var v = brightness / 255d;
        var chroma = v * s;
        var secondary = chroma * (1d - Math.Abs((h / 60d % 2d) - 1d));
        var match = v - chroma;
        var (red, green, blue) = h switch
        {
            < 60 => (chroma, secondary, 0d), < 120 => (secondary, chroma, 0d), < 180 => (0d, chroma, secondary),
            < 240 => (0d, secondary, chroma), < 300 => (secondary, 0d, chroma), _ => (chroma, 0d, secondary)
        };
        return Color.FromRgb(ToByte(red + match), ToByte(green + match), ToByte(blue + match));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Floor(value * 255d + 0.5d), 0d, 255d);
}
