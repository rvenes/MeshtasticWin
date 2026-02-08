using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using MeshtasticWin.Models;

namespace MeshtasticWin;

public static class AppState
{
    public static ObservableCollection<NodeLive> Nodes { get; } = new();
    public static ObservableCollection<MessageLive> Messages { get; } = new();
    public static bool EnableWebViewDevTools { get; set; }

    public static string? ConnectedNodeIdHex { get; private set; }
    public static event Action? ConnectedNodeChanged;

    public static void SetConnectedNodeIdHex(string? idHex)
    {
        if (string.IsNullOrWhiteSpace(idHex))
            idHex = null;

        if (string.Equals(ConnectedNodeIdHex, idHex, StringComparison.OrdinalIgnoreCase))
            return;

        ConnectedNodeIdHex = idHex;
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

    // null = Primary channel (broadcast), elles DM med denne node-id-en (IdHex, t.d. "0xd6c218df")
    public static string? ActiveChatPeerIdHex { get; private set; }

    public static event Action? ActiveChatChanged;

    public static void SetActiveChatPeer(string? peerIdHex)
    {
        // Normaliser litt
        if (string.IsNullOrWhiteSpace(peerIdHex))
            peerIdHex = null;

        if (string.Equals(ActiveChatPeerIdHex, peerIdHex, StringComparison.OrdinalIgnoreCase))
            return;

        ActiveChatPeerIdHex = peerIdHex;
        ActiveChatChanged?.Invoke();
    }
}
