using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshtasticWin.Models;
using MeshtasticWin.Services;

namespace MeshtasticWin;

public static class AppState
{
    private const string ShowPowerMetricsKey = "ShowPowerMetricsTab";
    private const string ShowDetectionSensorKey = "ShowDetectionSensorLogTab";
    private const string UnreadLastReadKey = "UnreadLastReadUtcByPeerJson";

    public static ObservableCollection<NodeLive> Nodes { get; } = new();
    public static ObservableCollection<MessageLive> Messages { get; } = new();
    public static event Action? SettingsChanged;

    private static bool _showPowerMetricsTab;
    public static bool ShowPowerMetricsTab
    {
        get => _showPowerMetricsTab;
        set
        {
            if (_showPowerMetricsTab == value) return;
            _showPowerMetricsTab = value;
            SettingsStore.SetBool(ShowPowerMetricsKey, value);
            SettingsChanged?.Invoke();
        }
    }

    private static bool _showDetectionSensorLogTab;
    public static bool ShowDetectionSensorLogTab
    {
        get => _showDetectionSensorLogTab;
        set
        {
            if (_showDetectionSensorLogTab == value) return;
            _showDetectionSensorLogTab = value;
            SettingsStore.SetBool(ShowDetectionSensorKey, value);
            SettingsChanged?.Invoke();
        }
    }

    public static string? ConnectedNodeIdHex { get; private set; }
    public static event Action? ConnectedNodeChanged;

    public static void SetConnectedNodeIdHex(string? idHex)
    {
        if (string.IsNullOrWhiteSpace(idHex))
            idHex = null;

        if (string.Equals(ConnectedNodeIdHex, idHex, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(idHex))
            {
                var currentName = Nodes.FirstOrDefault(n => string.Equals(n.IdHex, idHex, StringComparison.OrdinalIgnoreCase))?.Name;
                AppDataPaths.SetActiveNodeScope(idHex, currentName);
                RadioClient.Instance.RotateLiveLogForCurrentScope();
            }
            return;
        }

        ConnectedNodeIdHex = idHex;

        // Keep logs separated per connected node.
        if (!string.IsNullOrWhiteSpace(idHex))
        {
            var nodeName = Nodes.FirstOrDefault(n => string.Equals(n.IdHex, idHex, StringComparison.OrdinalIgnoreCase))?.Name;
            AppDataPaths.SetActiveNodeScope(idHex, nodeName);
            RadioClient.Instance.RotateLiveLogForCurrentScope();
        }

        ConnectedNodeChanged?.Invoke();
    }


// Unread tracking (for chat list indicators)
// Key: null => Primary channel, otherwise peer node IdHex (e.g. "0xd6c218df")
private static readonly object _unreadLock = new();
private static readonly Dictionary<string, DateTime> _lastReadUtcByPeer = new(StringComparer.OrdinalIgnoreCase);
private static readonly Dictionary<string, DateTime> _lastIncomingUtcByPeer = new(StringComparer.OrdinalIgnoreCase);

public static event Action<string?>? UnreadChanged;

public static bool HasUnread(string? peerIdHex)
{
    var key = NormalizePeerKey(peerIdHex);

    lock (_unreadLock)
    {
        _lastReadUtcByPeer.TryGetValue(key, out var read);
        _lastIncomingUtcByPeer.TryGetValue(key, out var inc);
        return inc > read;
    }
}

public static void MarkChatRead(string? peerIdHex)
{
    var key = NormalizePeerKey(peerIdHex);

    lock (_unreadLock)
    {
        _lastReadUtcByPeer[key] = DateTime.UtcNow;
    }

    PersistUnreadState();
    UnreadChanged?.Invoke(peerIdHex);
}

public static void NotifyIncomingMessage(string? peerIdHex, DateTime whenUtc)
{
    var key = NormalizePeerKey(peerIdHex);

    lock (_unreadLock)
    {
        _lastIncomingUtcByPeer.TryGetValue(key, out var prev);
        if (whenUtc > prev)
            _lastIncomingUtcByPeer[key] = whenUtc;
    }

    UnreadChanged?.Invoke(peerIdHex);
}

private static string NormalizePeerKey(string? peerIdHex)
    => string.IsNullOrWhiteSpace(peerIdHex) ? "" : peerIdHex.Trim();

    static AppState()
    {
        // Settings
        _showPowerMetricsTab = SettingsStore.GetBool(ShowPowerMetricsKey) ?? false;
        _showDetectionSensorLogTab = SettingsStore.GetBool(ShowDetectionSensorKey) ?? false;

        // Unread state (optional persistence for chat read markers)
        try
        {
            var json = SettingsStore.GetString(UnreadLastReadKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
                if (parsed is not null)
                {
                    lock (_unreadLock)
                    {
                        foreach (var (peer, ticks) in parsed)
                        {
                            if (string.IsNullOrWhiteSpace(peer) || ticks <= 0)
                                continue;
                            _lastReadUtcByPeer[peer] = new DateTime(ticks, DateTimeKind.Utc);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore settings corruption; defaults apply.
        }
    }

    private static void PersistUnreadState()
    {
        try
        {
            Dictionary<string, long> snapshot;
            lock (_unreadLock)
                snapshot = _lastReadUtcByPeer.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Ticks, StringComparer.OrdinalIgnoreCase);

            SettingsStore.SetString(UnreadLastReadKey, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // Ignore persistence errors.
        }
    }

    // null = Primary channel (broadcast), otherwise DM with this node id (IdHex, e.g. "0xd6c218df")
    public static string? ActiveChatPeerIdHex { get; private set; }

    public static event Action? ActiveChatChanged;

    public static void SetActiveChatPeer(string? peerIdHex)
    {
        // Normalize input.
        if (string.IsNullOrWhiteSpace(peerIdHex))
            peerIdHex = null;

        if (string.Equals(ActiveChatPeerIdHex, peerIdHex, StringComparison.OrdinalIgnoreCase))
            return;

        ActiveChatPeerIdHex = peerIdHex;
        ActiveChatChanged?.Invoke();
    }
}
