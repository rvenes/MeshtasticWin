using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace MeshtasticWin.Services;

public static class AppDataPaths
{
    private static bool _initialized;
    private static string? _basePath;

    public static string BasePath => _basePath ??= ResolveBasePath();

    public static string LogsPath => Path.Combine(BasePath, "Logs");

    public static string TraceroutePath => Path.Combine(LogsPath, "traceroute");

    public static string GpsLogsPath => Path.Combine(LogsPath, "gps");

    public static string GpsPath => GpsLogsPath;

    public static void EnsureCreated()
    {
        if (_initialized)
            return;

        _initialized = true;

        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(TraceroutePath);
        Directory.CreateDirectory(GpsLogsPath);

        Debug.WriteLine($"Log base path resolved to: {BasePath}");
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
