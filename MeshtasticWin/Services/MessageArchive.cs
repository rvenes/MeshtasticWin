using System;
using System.IO;
using System.Text;
using MeshtasticWin.Models;

namespace MeshtasticWin.Services;

public static class MessageArchive
{
    private static readonly object _lock = new();

    // %LOCALAPPDATA%\MeshtasticWin\Logs
    private static string BaseDir =>
        AppDataPaths.LogsPath;

    public static void Append(MessageLive msg, string? channelName = null, string? dmPeerIdHex = null)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);

            var date = DateTime.Now.ToString("yyyy-MM-dd");
            var safeChannel = Sanitize(channelName);

            string fileName;

            if (!string.IsNullOrWhiteSpace(dmPeerIdHex))
            {
                // DM per node (peer)
                fileName = $"dm_{Sanitize(dmPeerIdHex)}_{date}.log";
            }
            else if (!string.IsNullOrWhiteSpace(safeChannel))
            {
                // Channel file
                fileName = $"channel_{safeChannel}_{date}.log";
            }
            else
            {
                // Fallback: everything in one file
                fileName = $"all_{date}.log";
            }

            var path = Path.Combine(BaseDir, fileName);

            // Simple, robust line format
            // ISO time | header | text
            var line =
                $"{DateTimeOffset.Now:O} | {msg.Header} | {msg.Text.Replace("\r", " ").Replace("\n", " ")}";

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');

        return s.Trim().Replace(' ', '_');
    }
}
