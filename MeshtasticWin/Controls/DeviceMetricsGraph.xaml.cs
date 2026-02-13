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

public sealed partial class DeviceMetricsGraph : UserControl
{
    private const double PlotPadding = 6.0;
    private static readonly SolidColorBrush GridStrokeBrush = new(Color.FromArgb(56, 255, 255, 255));

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
        var width = PlotHost.ActualWidth;
        var height = PlotHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            GridCanvas.Children.Clear();
            BatteryLine.Points.Clear();
            ChannelLine.Points.Clear();
            AirtimeLine.Points.Clear();
            SetAxisValuesUnavailable();
            AxisStartText.Text = "—";
            AxisMidText.Text = "—";
            AxisEndText.Text = "—";
            return;
        }

        RenderGrid(width, height);

        var samples = _samples
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (samples.Count == 0)
        {
            BatteryLine.Points.Clear();
            ChannelLine.Points.Clear();
            AirtimeLine.Points.Clear();
            SetAxisValuesUnavailable();
            AxisStartText.Text = "—";
            AxisMidText.Text = "—";
            AxisEndText.Text = "—";
            return;
        }

        var minTs = samples.First().Timestamp;
        var maxTs = samples.Last().Timestamp;
        var midTs = minTs + TimeSpan.FromSeconds((maxTs - minTs).TotalSeconds / 2.0);
        var totalSeconds = Math.Max(1, (maxTs - minTs).TotalSeconds);
        AxisStartText.Text = FormatAxisTime(minTs);
        AxisMidText.Text = FormatAxisTime(midTs);
        AxisEndText.Text = FormatAxisTime(maxTs);

        var hasVoltage = samples.Any(s => s.BatteryVolts.HasValue);
        var batteryMin = hasVoltage ? 3.0 : 0.0;
        var batteryMax = hasVoltage ? 4.3 : 100.0;
        var batteryMid = batteryMin + ((batteryMax - batteryMin) / 2.0);
        BatteryAxisTopText.Text = hasVoltage ? $"{batteryMax:0.0}V" : $"{batteryMax:0}%";
        BatteryAxisMidText.Text = hasVoltage ? $"{batteryMid:0.0}V" : $"{batteryMid:0}%";
        BatteryAxisBottomText.Text = hasVoltage ? $"{batteryMin:0.0}V" : $"{batteryMin:0}%";
        ChannelAxisTopText.Text = "100%";
        ChannelAxisMidText.Text = "50%";
        ChannelAxisBottomText.Text = "0%";
        AirtimeAxisTopText.Text = "100%";
        AirtimeAxisMidText.Text = "50%";
        AirtimeAxisBottomText.Text = "0%";

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

    private void SetAxisValuesUnavailable()
    {
        BatteryAxisTopText.Text = "—";
        BatteryAxisMidText.Text = "—";
        BatteryAxisBottomText.Text = "—";
        ChannelAxisTopText.Text = "—";
        ChannelAxisMidText.Text = "—";
        ChannelAxisBottomText.Text = "—";
        AirtimeAxisTopText.Text = "—";
        AirtimeAxisMidText.Text = "—";
        AirtimeAxisBottomText.Text = "—";
    }

    private void RenderGrid(double width, double height)
    {
        GridCanvas.Children.Clear();

        var horizontalDivisions = 4;
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

        var verticalDivisions = 4;
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
        var points = new List<Windows.Foundation.Point>(samples.Count);
        var range = Math.Max(1e-6, maxY - minY);
        var usableWidth = Math.Max(1, width - (2 * PlotPadding));
        var usableHeight = Math.Max(1, height - (2 * PlotPadding));

        foreach (var sample in samples)
        {
            var value = selector(sample);
            if (!value.HasValue)
                continue;

            var seconds = Math.Max(0, (sample.Timestamp - minTs).TotalSeconds);
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
