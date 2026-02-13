using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MeshtasticWin.Models;
using Windows.UI;

namespace MeshtasticWin.Controls;

public sealed partial class EnvironmentMetricsGraph : UserControl
{
    private const double PlotPadding = 6.0;
    private static readonly SolidColorBrush GridStrokeBrush = new(Color.FromArgb(56, 255, 255, 255));
    private IReadOnlyList<EnvironmentMetricSample> _samples = Array.Empty<EnvironmentMetricSample>();

    public EnvironmentMetricsGraph()
    {
        InitializeComponent();
        SizeChanged += EnvironmentMetricsGraph_SizeChanged;
    }

    public void SetSamples(IReadOnlyList<EnvironmentMetricSample> samples)
    {
        _samples = samples ?? Array.Empty<EnvironmentMetricSample>();
        RenderChart();
    }

    private void EnvironmentMetricsGraph_SizeChanged(object sender, SizeChangedEventArgs e)
        => RenderChart();

    private void RenderChart()
    {
        var width = PlotHost.ActualWidth;
        var height = PlotHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            GridCanvas.Children.Clear();
            TemperatureLine.Points.Clear();
            HumidityLine.Points.Clear();
            PressureLine.Points.Clear();
            SetAxisValuesUnavailable();
            AxisStartText.Text = "—";
            AxisMidText.Text = "—";
            AxisEndText.Text = "—";
            return;
        }

        RenderGrid(width, height);

        var samples = _samples
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        if (samples.Count == 0)
        {
            TemperatureLine.Points.Clear();
            HumidityLine.Points.Clear();
            PressureLine.Points.Clear();
            SetAxisValuesUnavailable();
            AxisStartText.Text = "—";
            AxisMidText.Text = "—";
            AxisEndText.Text = "—";
            return;
        }

        var minTs = samples.First().TimestampUtc;
        var maxTs = samples.Last().TimestampUtc;
        var midTs = minTs + TimeSpan.FromSeconds((maxTs - minTs).TotalSeconds / 2.0);
        var totalSeconds = Math.Max(1, (maxTs - minTs).TotalSeconds);
        AxisStartText.Text = FormatAxisTime(minTs);
        AxisMidText.Text = FormatAxisTime(midTs);
        AxisEndText.Text = FormatAxisTime(maxTs);

        var (tempMin, tempMax) = ResolveRange(samples.Select(s => s.TemperatureC), defaultMin: -20, defaultMax: 50, padding: 1);
        var (pressureMin, pressureMax) = ResolveRange(samples.Select(s => s.BarometricPressure), defaultMin: 950, defaultMax: 1050, padding: 0.5);
        var tempMid = tempMin + ((tempMax - tempMin) / 2.0);
        var pressureMid = pressureMin + ((pressureMax - pressureMin) / 2.0);

        TemperatureAxisTopText.Text = $"{tempMax:0.0}C";
        TemperatureAxisMidText.Text = $"{tempMid:0.0}C";
        TemperatureAxisBottomText.Text = $"{tempMin:0.0}C";

        HumidityAxisTopText.Text = "100%";
        HumidityAxisMidText.Text = "50%";
        HumidityAxisBottomText.Text = "0%";

        PressureAxisTopText.Text = $"{pressureMax:0.0}hPa";
        PressureAxisMidText.Text = $"{pressureMid:0.0}hPa";
        PressureAxisBottomText.Text = $"{pressureMin:0.0}hPa";

        TemperatureLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.TemperatureC, tempMin, tempMax);

        HumidityLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.RelativeHumidity, 0, 100);

        PressureLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.BarometricPressure, pressureMin, pressureMax);
    }

    private void SetAxisValuesUnavailable()
    {
        TemperatureAxisTopText.Text = "—";
        TemperatureAxisMidText.Text = "—";
        TemperatureAxisBottomText.Text = "—";
        HumidityAxisTopText.Text = "—";
        HumidityAxisMidText.Text = "—";
        HumidityAxisBottomText.Text = "—";
        PressureAxisTopText.Text = "—";
        PressureAxisMidText.Text = "—";
        PressureAxisBottomText.Text = "—";
    }

    private void RenderGrid(double width, double height)
    {
        GridCanvas.Children.Clear();

        const int horizontalDivisions = 4;
        for (var i = 0; i <= horizontalDivisions; i++)
        {
            var y = PlotPadding + ((height - (2 * PlotPadding)) * i / horizontalDivisions);
            GridCanvas.Children.Add(new Line
            {
                X1 = PlotPadding,
                Y1 = y,
                X2 = width - PlotPadding,
                Y2 = y,
                Stroke = GridStrokeBrush,
                StrokeThickness = 1
            });
        }

        const int verticalDivisions = 4;
        for (var i = 0; i <= verticalDivisions; i++)
        {
            var x = PlotPadding + ((width - (2 * PlotPadding)) * i / verticalDivisions);
            GridCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = PlotPadding,
                X2 = x,
                Y2 = height - PlotPadding,
                Stroke = GridStrokeBrush,
                StrokeThickness = 1
            });
        }
    }

    private static string FormatAxisTime(DateTime timestamp)
    {
        if (timestamp == DateTime.MinValue)
            return "—";

        var local = timestamp.ToLocalTime();
        return local.ToString("HH:mm:ss");
    }

    private static (double Min, double Max) ResolveRange(
        IEnumerable<double?> values,
        double defaultMin,
        double defaultMax,
        double padding)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0)
            return (defaultMin, defaultMax);

        var min = present.Min() - padding;
        var max = present.Max() + padding;
        if (Math.Abs(max - min) < 0.001)
        {
            min -= 1;
            max += 1;
        }

        return (min, max);
    }

    private static PointCollection BuildPoints(
        IReadOnlyList<EnvironmentMetricSample> samples,
        double width,
        double height,
        double totalSeconds,
        DateTime minTs,
        Func<EnvironmentMetricSample, double?> selector,
        double minY,
        double maxY)
    {
        var points = new List<Windows.Foundation.Point>(samples.Count);
        var range = Math.Max(1e-6, maxY - minY);
        var usableWidth = Math.Max(1, width - (2 * PlotPadding));
        var usableHeight = Math.Max(1, height - (2 * PlotPadding));

        foreach (var sample in samples)
        {
            var value = selector(sample);
            if (!value.HasValue)
                continue;

            var seconds = Math.Max(0, (sample.TimestampUtc - minTs).TotalSeconds);
            var x = PlotPadding + usableWidth * (seconds / totalSeconds);
            var clamped = Math.Max(minY, Math.Min(maxY, value.Value));
            var normalized = (clamped - minY) / range;
            var y = PlotPadding + usableHeight - (usableHeight * normalized);
            points.Add(new Windows.Foundation.Point(x, y));
        }

        var maxPoints = Math.Max(64, (int)Math.Round(usableWidth * 2));
        if (points.Count <= maxPoints)
        {
            var all = new PointCollection();
            foreach (var point in points)
                all.Add(point);
            return all;
        }

        var reduced = new List<Windows.Foundation.Point>(maxPoints);
        var lastIndex = points.Count - 1;
        for (var i = 0; i < maxPoints; i++)
        {
            var index = (int)Math.Round(i * (lastIndex / (double)(maxPoints - 1)));
            reduced.Add(points[index]);
        }

        var downsampled = new PointCollection();
        foreach (var point in reduced)
            downsampled.Add(point);
        return downsampled;
    }
}
