using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace MeshtasticWin.Services;

public static class SettingsStore
{
    private static readonly object _lock = new();
    private static Dictionary<string, string>? _fallback;
    private static bool _fallbackLoaded;

    private static string FallbackFilePath
        => Path.Combine(AppDataPaths.BasePath, "settings_fallback.json");

    private static string LegacyConnectFallbackFilePath
        => Path.Combine(AppDataPaths.BasePath, "connect_settings.json");

    public static string? GetString(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        lock (_lock)
        {
            if (TryGetLocalValue(key, out var value))
                return value as string;

            EnsureFallbackLoaded();
            if (_fallback is not null && _fallback.TryGetValue(key, out var v))
                return v;

            return null;
        }
    }

    public static void SetString(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_lock)
        {
            if (!TrySetLocalValue(key, value))
            {
                EnsureFallbackLoaded();
                _fallback ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (value is null)
                    _fallback.Remove(key);
                else
                    _fallback[key] = value;

                PersistFallback();
            }
        }
    }

    public static bool? GetBool(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        lock (_lock)
        {
            if (TryGetLocalValue(key, out var value))
            {
                if (value is bool b)
                    return b;
                if (value is int i)
                    return i != 0;
            }

            EnsureFallbackLoaded();
            if (_fallback is not null && _fallback.TryGetValue(key, out var v))
            {
                if (bool.TryParse(v, out var asBool))
                    return asBool;
                if (int.TryParse(v, out var asInt))
                    return asInt != 0;
            }

            return null;
        }
    }

    public static void SetBool(string key, bool value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_lock)
        {
            if (!TrySetLocalValue(key, value))
            {
                EnsureFallbackLoaded();
                _fallback ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _fallback[key] = value ? "true" : "false";
                PersistFallback();
            }
        }
    }

    private static void EnsureFallbackLoaded()
    {
        if (_fallbackLoaded)
            return;
        _fallbackLoaded = true;

        try
        {
            if (!File.Exists(FallbackFilePath))
            {
                // Legacy migration (ConnectPage used connect_settings.json previously).
                if (File.Exists(LegacyConnectFallbackFilePath))
                {
                    var legacyJson = File.ReadAllText(LegacyConnectFallbackFilePath);
                    _fallback = JsonSerializer.Deserialize<Dictionary<string, string>>(legacyJson)
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    PersistFallback();
                    return;
                }

                _fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var json = File.ReadAllText(FallbackFilePath);
            _fallback = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void PersistFallback()
    {
        try
        {
            var dir = Path.GetDirectoryName(FallbackFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_fallback ?? new Dictionary<string, string>());
            File.WriteAllText(FallbackFilePath, json);
        }
        catch
        {
            // Ignore persistence errors.
        }
    }

    private static bool TryGetLocalValue(string key, out object? value)
    {
        value = null;

        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            return settings.Values.TryGetValue(key, out value);
        }
        catch
        {
            // Unpackaged / test contexts may not support LocalSettings.
            value = null;
            return false;
        }
    }

    private static bool TrySetLocalValue(string key, object? value)
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (value is null)
            {
                _ = settings.Values.Remove(key);
                return true;
            }

            settings.Values[key] = value;
            return true;
        }
        catch
        {
            // Unpackaged / test contexts may not support LocalSettings.
            return false;
        }
    }
}
