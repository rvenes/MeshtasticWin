using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MeshtasticWin.Services;

public enum NodeLogType
{
    DeviceMetrics,
    EnvironmentMetrics,
    PowerMetrics,
    TraceRoute,
    DetectionSensor
}

public static class NodeLogArchive
{
    private static readonly object _gate = new();

    private static string BaseDir => AppDataPaths.LogsPath;

    private static readonly Dictionary<NodeLogType, string> FolderNames = new()
    {
        [NodeLogType.DeviceMetrics] = "device_metrics",
        [NodeLogType.EnvironmentMetrics] = "environment_metrics",
        [NodeLogType.PowerMetrics] = "power_metrics",
        [NodeLogType.TraceRoute] = "traceroute",
        [NodeLogType.DetectionSensor] = "detection_sensor"
    };

    public static void EnsureBaseFolders()
    {
        AppDataPaths.EnsureCreated();
        foreach (var entry in FolderNames)
            Directory.CreateDirectory(Path.Combine(BaseDir, entry.Value));

        Directory.CreateDirectory(GpsArchive.RootFolder);
    }

    public static void Append(NodeLogType type, string idHex, DateTime tsUtc, string summary)
    {
        var path = FilePathFor(type, idHex);

        if (tsUtc.Kind != DateTimeKind.Utc)
            tsUtc = DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc);

        var safeSummary = (summary ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        var line = $"{tsUtc.ToString("o", CultureInfo.InvariantCulture)} | {safeSummary}";

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static string[] ReadTail(NodeLogType type, string idHex, int maxLines = 200)
    {
        var path = FilePathFor(type, idHex);
        if (!File.Exists(path))
            return Array.Empty<string>();

        string[] lines;
        lock (_gate)
        {
            lines = File.ReadAllLines(path);
        }

        return lines.Length <= maxLines ? lines : lines[^maxLines..];
    }

    private static string FilePathFor(NodeLogType type, string idHex)
    {
        EnsureBaseFolders();

        var safe = (idHex ?? "").Trim();
        if (safe.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            safe = safe[2..];

        safe = new string(safe.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            safe = "unknown";

        var folder = FolderNames[type];
        return Path.Combine(BaseDir, folder, $"0x{safe.ToUpperInvariant()}.log");
    }
}
