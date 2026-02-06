using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeshtasticWin.Models;

namespace MeshtasticWin.Controls;

public sealed partial class DeviceMetricsGraph : UserControl
{
    private IReadOnlyList<DeviceMetricSample> _samples = Array.Empty<DeviceMetricSample>();

    public DeviceMetricsGraph()
    {
        InitializeComponent();
        SizeChanged += DeviceMetricsGraph_SizeChanged;
    }

    public void SetSamples(IReadOnlyList<DeviceMetricSample> samples)
    {
        _samples = samples ?? Array.Empty<DeviceMetricSample>();
        RenderChart();
    }

    private void DeviceMetricsGraph_SizeChanged(object sender, SizeChangedEventArgs e)
        => RenderChart();

    private void RenderChart()
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            BatteryLine.Points.Clear();
            ChannelLine.Points.Clear();
            AirtimeLine.Points.Clear();
            return;
        }

        var samples = _samples
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (samples.Count == 0)
        {
            BatteryLine.Points.Clear();
            ChannelLine.Points.Clear();
            AirtimeLine.Points.Clear();
            return;
        }

        var minTs = samples.First().Timestamp;
        var maxTs = samples.Last().Timestamp;
        var totalSeconds = Math.Max(1, (maxTs - minTs).TotalSeconds);

        var hasVoltage = samples.Any(s => s.BatteryVolts.HasValue);
        var batteryMin = hasVoltage ? 3.0 : 0.0;
        var batteryMax = hasVoltage ? 4.3 : 100.0;

        BatteryLine.Points = BuildPoints(samples, width, height, totalSeconds, minTs,
            sample => hasVoltage ? sample.BatteryVolts : sample.BatteryPercent,
            batteryMin, batteryMax);

        ChannelLine.Points = BuildPoints(samples, width, height, totalSeconds, minTs,
            sample => sample.ChannelUtilization,
            0, 100);

        AirtimeLine.Points = BuildPoints(samples, width, height, totalSeconds, minTs,
            sample => sample.Airtime,
            0, 100);
    }

    private static PointCollection BuildPoints(
        IReadOnlyList<DeviceMetricSample> samples,
        double width,
        double height,
        double totalSeconds,
        DateTime minTs,
        Func<DeviceMetricSample, double?> selector,
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

            var seconds = Math.Max(0, (sample.Timestamp - minTs).TotalSeconds);
            var x = width * (seconds / totalSeconds);
            var clamped = Math.Max(minY, Math.Min(maxY, value.Value));
            var normalized = (clamped - minY) / range;
            var y = height - (height * normalized);
            points.Add(new Windows.Foundation.Point(x, y));
        }

        return points;
    }
}
