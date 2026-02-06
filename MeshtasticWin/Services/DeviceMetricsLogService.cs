using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MeshtasticWin.Models;

namespace MeshtasticWin.Services;

public static class DeviceMetricsLogService
{
    private const int DefaultMaxSamples = 2000;
    private static readonly object _gate = new();
    private static readonly Dictionary<string, List<DeviceMetricSample>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static event Action<string, DeviceMetricSample>? SampleAdded;

    public static IReadOnlyList<DeviceMetricSample> GetSamples(string idHex, int maxSamples = DefaultMaxSamples)
    {
        var key = NormalizeNodeId(idHex);
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var list))
            {
                list = LoadSamples(key, maxSamples);
                _cache[key] = list;
            }

            return list.ToList();
        }
    }

    public static void AppendSample(string idHex, DeviceMetricSample sample, int maxSamples = DefaultMaxSamples)
    {
        var key = NormalizeNodeId(idHex);
        var path = GetLogPath(key);

        if (sample.Timestamp.Kind != DateTimeKind.Utc)
            sample = sample with { Timestamp = sample.Timestamp.ToUniversalTime() };

        var line = string.Join(",",
            sample.Timestamp.ToString("o", CultureInfo.InvariantCulture),
            FormatNullable(sample.BatteryVolts, "0.###"),
            FormatNullable(sample.BatteryPercent, "0.##"),
            FormatNullable(sample.ChannelUtilization, "0.##"),
            FormatNullable(sample.Airtime, "0.##"),
            sample.IsPowered.HasValue ? (sample.IsPowered.Value ? "1" : "0") : "");

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                File.AppendAllText(path, "timestamp_utc,battery_volts,battery_percent,channel_utilization,airtime,is_powered" + Environment.NewLine);

            File.AppendAllText(path, line + Environment.NewLine);

            if (!_cache.TryGetValue(key, out var list))
            {
                list = new List<DeviceMetricSample>();
                _cache[key] = list;
            }

            list.Insert(0, sample);
            if (list.Count > maxSamples)
                list.RemoveRange(maxSamples, list.Count - maxSamples);
        }

        SampleAdded?.Invoke(key, sample);
    }

    public static void ClearSamples(string idHex)
    {
        var key = NormalizeNodeId(idHex);
        var path = GetLogPath(key);
        lock (_gate)
        {
            _cache.Remove(key);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public static string GetLogPath(string idHex)
    {
        var safe = NormalizeNodeId(idHex);
        var baseDir = AppDataPaths.LogsPath;
        Directory.CreateDirectory(Path.Combine(baseDir, "device_metrics"));
        return Path.Combine(baseDir, "device_metrics", $"{safe}.log");
    }

    private static List<DeviceMetricSample> LoadSamples(string idHex, int maxSamples)
    {
        var path = GetLogPath(idHex);
        if (!File.Exists(path))
            return new List<DeviceMetricSample>();

        string[] lines;
        lock (_gate)
        {
            lines = File.ReadAllLines(path);
        }

        var slice = lines.Length <= maxSamples ? lines : lines[^maxSamples..];
        var list = new List<DeviceMetricSample>(slice.Length);

        foreach (var raw in slice)
        {
            var line = raw?.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("timestamp_utc", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split(',');
            if (parts.Length < 1)
                continue;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tsUtc))
                continue;

            var batteryVolts = ParseNullable(parts, 1);
            var batteryPercent = ParseNullable(parts, 2);
            var channelUtil = ParseNullable(parts, 3);
            var airtime = ParseNullable(parts, 4);
            bool? powered = null;
            if (parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]))
                powered = parts[5].Trim() == "1";

            var sample = new DeviceMetricSample(DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc), batteryVolts, channelUtil, airtime, powered)
            {
                BatteryPercent = batteryPercent
            };
            list.Add(sample);
        }

        return list
            .OrderByDescending(s => s.Timestamp)
            .Take(maxSamples)
            .ToList();
    }

    private static string NormalizeNodeId(string idHex)
    {
        var safe = (idHex ?? "").Trim();
        if (safe.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            safe = safe[2..];

        safe = new string(safe.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            safe = "UNKNOWN";

        return $"0x{safe.ToUpperInvariant()}";
    }

    private static string FormatNullable(double? value, string format)
        => value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "";

    private static double? ParseNullable(string[] parts, int index)
    {
        if (parts.Length <= index)
            return null;

        var text = parts[index];
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;

        return null;
    }
}
