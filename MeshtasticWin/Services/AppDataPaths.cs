using System;
using System.Diagnostics;
using System.IO;

namespace MeshtasticWin.Services;

public static class AppDataPaths
{
    public static string BasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshtasticWin");

    public static string LogsPath => Path.Combine(BasePath, "Logs");

    public static string TraceroutePath => Path.Combine(LogsPath, "traceroute");

    public static string GpsPath => Path.Combine(LogsPath, "gps");

    public static void EnsureCreated()
    {
        CreateDirectory(BasePath);
        CreateDirectory(LogsPath);
        CreateDirectory(TraceroutePath);
        CreateDirectory(GpsPath);
        Debug.WriteLine($"MeshtasticWin BasePath: {BasePath}");
    }

    private static void CreateDirectory(string path)
    {
        Debug.WriteLine($"Creating dir: {path}");
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create dir: {path} :: {ex}");
            throw;
        }
    }
}
