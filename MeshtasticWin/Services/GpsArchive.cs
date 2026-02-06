using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MeshtasticWin.Services;

public static class GpsArchive
{
    // This type was missing in earlier revisions; keep it here to ensure it is always available.
    public sealed record PositionPoint(double Lat, double Lon, DateTime TsUtc, double? Alt, string Src);

    private static readonly object _gate = new();

    public static string RootFolder => AppDataPaths.GpsPath;

    private static string FilePathFor(string idHex)
    {
        Directory.CreateDirectory(RootFolder);

        var safe = (idHex ?? "").Trim();
        if (safe.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            safe = safe[2..];

        // 0xAABBCCDD -> 0xAABBCCDD.log
        safe = new string(safe.Where(ch => char.IsLetterOrDigit(ch)).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            safe = "unknown";

        return Path.Combine(RootFolder, $"0x{safe.ToUpperInvariant()}.log");
    }

    public static bool HasLog(string idHex)
    {
        var path = FilePathFor(idHex);
        return File.Exists(path);
    }

    // Format (TS in ISO-8601 UTC):
    // 2026-02-04T06:20:54.1234567Z;61.1893100;7.0264210;122;nodeinfo_bootstrap
    public static void Append(string idHex, double lat, double lon, DateTime tsUtc, double? alt, string src)
    {
        var path = FilePathFor(idHex);

        // Always UTC in file
        if (tsUtc.Kind != DateTimeKind.Utc)
            tsUtc = DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc);

        var line =
            FormatLine(new PositionPoint(lat, lon, tsUtc, alt, src));

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static void WriteAll(string idHex, IEnumerable<PositionPoint> points)
    {
        var path = FilePathFor(idHex);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir, $"{Path.GetFileName(path)}.tmp");
        var lines = points
            .OrderBy(p => p.TsUtc)
            .Select(FormatLine)
            .ToArray();

        lock (_gate)
        {
            File.WriteAllLines(tempPath, lines);
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
    }

    public static List<PositionPoint> ReadAll(string idHex, int maxPoints = 5000)
    {
        var path = FilePathFor(idHex);
        if (!File.Exists(path))
            return new List<PositionPoint>();

        string[] lines;
        lock (_gate)
        {
            lines = File.ReadAllLines(path);
        }

        // Read the last maxPoints lines (performance for large files).
        var slice = lines.Length <= maxPoints ? lines : lines[^maxPoints..];

        var list = new List<PositionPoint>(slice.Length);

        foreach (var raw in slice)
        {
            var line = raw?.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(';');
            if (parts.Length < 3)
                continue;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var ts))
                continue;

            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                continue;

            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                continue;

            double? alt = null;
            if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                alt = a;

            var src = parts.Length >= 5 ? parts[4] : "";

            list.Add(new PositionPoint(lat, lon, DateTime.SpecifyKind(ts, DateTimeKind.Utc), alt, src));
        }

        return list;
    }

    private static string FormatLine(PositionPoint point)
    {
        var tsUtc = point.TsUtc;
        if (tsUtc.Kind != DateTimeKind.Utc)
            tsUtc = DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc);

        return tsUtc.ToString("o", CultureInfo.InvariantCulture) + ";" +
               point.Lat.ToString("0.0000000", CultureInfo.InvariantCulture) + ";" +
               point.Lon.ToString("0.0000000", CultureInfo.InvariantCulture) + ";" +
               (point.Alt.HasValue ? point.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture) : "") + ";" +
               (point.Src ?? "");
    }
}
