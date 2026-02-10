using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeshtasticWin.Models;

namespace MeshtasticWin.Controls;

public sealed partial class EnvironmentMetricsGraph : UserControl
{
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
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            TemperatureLine.Points.Clear();
            HumidityLine.Points.Clear();
            PressureLine.Points.Clear();
            return;
        }

        var samples = _samples
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        if (samples.Count == 0)
        {
            TemperatureLine.Points.Clear();
            HumidityLine.Points.Clear();
            PressureLine.Points.Clear();
            return;
        }

        var minTs = samples.First().TimestampUtc;
        var maxTs = samples.Last().TimestampUtc;
        var totalSeconds = Math.Max(1, (maxTs - minTs).TotalSeconds);

        var (tempMin, tempMax) = ResolveRange(samples.Select(s => s.TemperatureC), defaultMin: -20, defaultMax: 50, padding: 1);
        var (pressureMin, pressureMax) = ResolveRange(samples.Select(s => s.BarometricPressure), defaultMin: 950, defaultMax: 1050, padding: 0.5);

        TemperatureLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.TemperatureC, tempMin, tempMax);

        HumidityLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.RelativeHumidity, 0, 100);

        PressureLine.Points = BuildPoints(
            samples, width, height, totalSeconds, minTs, s => s.BarometricPressure, pressureMin, pressureMax);
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
        var points = new PointCollection();
        var range = Math.Max(1e-6, maxY - minY);

        foreach (var sample in samples)
        {
            var value = selector(sample);
            if (!value.HasValue)
                continue;

            var seconds = Math.Max(0, (sample.TimestampUtc - minTs).TotalSeconds);
            var x = width * (seconds / totalSeconds);
            var clamped = Math.Max(minY, Math.Min(maxY, value.Value));
            var normalized = (clamped - minY) / range;
            var y = height - (height * normalized);
            points.Add(new Windows.Foundation.Point(x, y));
        }

        return points;
    }
}
