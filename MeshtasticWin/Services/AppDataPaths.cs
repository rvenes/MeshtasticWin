using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace MeshtasticWin.Services;

public static class AppDataPaths
{
    private static string? _basePath;
    private static readonly object _scopeLock = new();
    private static readonly HashSet<string> _ensuredPaths = new(StringComparer.OrdinalIgnoreCase);
    private static string _activeNodeScope = "UnknownNode";

    public static string BasePath => _basePath ??= ResolveBasePath();

    // Root for node-scoped logging. Each connected node gets its own subfolder.
    public static string LogsRootPath => Path.Combine(BasePath, "NodeLogs");

    // Preferred live debug log folder name (English), with legacy fallback.
    public static string DebugLogsRootPath
    {
        get
        {
            var preferred = Path.Combine(BasePath, "DebugLogs");
            var legacy = Path.Combine(BasePath, "Debuglogg");

            try
            {
                if (Directory.Exists(legacy) && !Directory.Exists(preferred))
                    return legacy;
            }
            catch
            {
                // Ignore probing failures.
            }

            return preferred;
        }
    }

    public static string ActiveNodeScope
    {
        get
        {
            lock (_scopeLock)
                return _activeNodeScope;
        }
    }

    public static string LogsPath => Path.Combine(LogsRootPath, ActiveNodeScope, "Logs");

    public static string TraceroutePath => Path.Combine(LogsPath, "traceroute");

    public static string GpsLogsPath => Path.Combine(LogsPath, "gps");

    public static string GpsPath => GpsLogsPath;

    public static void SetActiveNodeScope(string? idHex, string? nodeName = null)
    {
        var scope = BuildNodeScope(idHex, nodeName);
        lock (_scopeLock)
            _activeNodeScope = scope;

        EnsureCreated();
    }

    public static void EnsureCreated()
    {
        EnsurePath(BasePath);
        EnsurePath(LogsRootPath);
        EnsurePath(DebugLogsRootPath);
        EnsurePath(Path.Combine(LogsRootPath, ActiveNodeScope));
        EnsurePath(LogsPath);
        EnsurePath(TraceroutePath);
        EnsurePath(GpsLogsPath);

        Debug.WriteLine($"Log base path resolved to: {BasePath}");
    }

    private static void EnsurePath(string path)
    {
        lock (_scopeLock)
        {
            if (_ensuredPaths.Contains(path))
                return;

            Directory.CreateDirectory(path);
            _ensuredPaths.Add(path);
        }
    }

    private static string BuildNodeScope(string? idHex, string? nodeName)
    {
        var safeId = SanitizeNodeId(idHex);
        if (string.IsNullOrWhiteSpace(safeId))
            return "UnknownNode";

        // Stable scope key: always node id only.
        return safeId;
    }

    private static string SanitizeNodeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        foreach (var c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '_');

        text = new string(text.Where(char.IsLetterOrDigit).ToArray());
        return text.ToLowerInvariant();
    }

    private static string ResolveBasePath()
    {
        if (Packaging.IsPackaged())
        {
            var localFolder = ApplicationData.Current.LocalFolder.Path;
            return Path.Combine(localFolder, "MeshtasticWin");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshtasticWin");
    }

    private static bool IsPackaged()
    {
        var length = 0u;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
