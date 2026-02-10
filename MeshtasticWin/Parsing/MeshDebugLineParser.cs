using MeshtasticWin.Models;
using MeshtasticWin.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MeshtasticWin.Parsing;

public static class MeshDebugLineParser
{
    private const int MaxMessages = 500;
    private const int MaxPacketMetaEntries = 1024;
    private static readonly TimeSpan PacketMetaTtl = TimeSpan.FromMinutes(20);
    private static readonly object PacketMetaLock = new();
    private static readonly Dictionary<uint, (uint ToNode, DateTime SeenUtc)> TextPacketMetaById = new();

    // Matches fr=0xd6c218df or from=0xd6c218df or "from 0xd6c218df".
    private static readonly Regex FrRegex =
        new(@"(?:\bfr=0x(?<id>[0-9a-fA-F]+)\b|\bfrom=0x(?<id>[0-9a-fA-F]+)\b|\bfrom 0x(?<id>[0-9a-fA-F]+)\b)",
            RegexOptions.Compiled);

    private static readonly Regex ReceivedTextRegex =
        new(@"\[Router\].*\bReceived text msg from=0x(?<from>[0-9a-fA-F]+)\s*,\s*id=0x(?<pid>[0-9a-fA-F]+)\s*,\s*msg=(?<msg>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DownloadedTextPacketRegex =
        new(@"\bphone downloaded packet \(id=0x(?<id>[0-9a-fA-F]+)\s+fr=0x(?<from>[0-9a-fA-F]+)\s+to=0x(?<to>[0-9a-fA-F]+).*\bPortnum=(?<port>\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GenericTextPacketRegex =
        new(@"\bid=0x(?<id>[0-9a-fA-F]+)\b.*?\bto=0x(?<to>[0-9a-fA-F]+)\b.*?\bPortnum=(?<port>\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SnrRegex =
        new(@"\bnSNR=(?<snr>-?\d+(\.\d+)?)\b", RegexOptions.Compiled);

    private static readonly Regex RssiRegex =
        new(@"\bnRSSI=(?<rssi>-?\d+)\b", RegexOptions.Compiled);

    private static readonly Regex HopRegex =
        new(@"\bHopLim=(?<hop>\d+)\b", RegexOptions.Compiled);

    private static readonly Regex ChRegex =
        new(@"\bCh=0x(?<ch>[0-9a-fA-F]+)\b", RegexOptions.Compiled);

    public static void Consume(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        CapturePacketMetadata(line);
        TryCaptureIncomingTextFromDebugLine(line);

        var m = FrRegex.Match(line);
        if (!m.Success)
            return;

        var idHex = $"0x{NormalizeNodeId(m.Groups["id"].Value)}";

        // Find or create node.
        var node = AppState.Nodes.FirstOrDefault(n => n.IdHex == idHex);
        if (node is null)
        {
            node = new NodeLive(idHex);
            AppState.Nodes.Insert(0, node);
        }

        node.Touch();

        // Extract signal data when present.
        var snrM = SnrRegex.Match(line);
        if (snrM.Success)
            node.SNR = snrM.Groups["snr"].Value;

        var rssiM = RssiRegex.Match(line);
        if (rssiM.Success)
            node.RSSI = rssiM.Groups["rssi"].Value;

        // Build a compact "Sub" line similar to the macOS app.
        string sub = "Seen on mesh";
        var hopM = HopRegex.Match(line);
        var chM = ChRegex.Match(line);

        if (snrM.Success || rssiM.Success || hopM.Success || chM.Success)
        {
            sub = "Signal";
            if (snrM.Success) sub += $" • SNR {node.SNR}";
            if (rssiM.Success) sub += $" • RSSI {node.RSSI}";
            if (hopM.Success) sub += $" • HopLim {hopM.Groups["hop"].Value}";
            if (chM.Success) sub += $" • Ch 0x{chM.Groups["ch"].Value}";
        }

        node.Sub = sub;
    }

    public static void CapturePacketMetadata(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var m = DownloadedTextPacketRegex.Match(line);
        if (!m.Success)
            m = GenericTextPacketRegex.Match(line);
        if (!m.Success)
            return;

        if (!int.TryParse(m.Groups["port"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var portNum) || portNum != 1)
            return;

        if (!uint.TryParse(m.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packetId) || packetId == 0)
            return;

        if (!uint.TryParse(m.Groups["to"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var toNode))
            return;

        lock (PacketMetaLock)
        {
            TextPacketMetaById[packetId] = (toNode, DateTime.UtcNow);
            TrimPacketMeta(DateTime.UtcNow);
        }
    }

    private static void TryCaptureIncomingTextFromDebugLine(string line)
    {
        if (line.Contains("[SerialConsole]", StringComparison.OrdinalIgnoreCase))
            return;

        var m = ReceivedTextRegex.Match(line);
        if (!m.Success)
            return;

        var fromIdHex = $"0x{NormalizeNodeId(m.Groups["from"].Value)}";
        if (string.Equals(fromIdHex, "0x00000000", StringComparison.OrdinalIgnoreCase))
            return;

        var text = (m.Groups["msg"].Value ?? string.Empty).Trim();
        if (text.Length == 0)
            return;

        uint packetId = 0;
        _ = uint.TryParse(m.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out packetId);

        var toNodeNum = 0xFFFFFFFFu;
        if (packetId != 0 && TryGetToNodeForPacket(packetId, out var mappedToNode))
            toNodeNum = mappedToNode;
        var toIdHex = $"0x{toNodeNum:x8}";
        var toName = toNodeNum == 0xFFFFFFFF ? "Primary" : toIdHex;

        if (packetId != 0)
        {
            var existingByPacket = AppState.Messages.FirstOrDefault(x => !x.IsMine && x.PacketId == packetId);
            if (existingByPacket is not null)
            {
                var existingTo = existingByPacket.ToIdHex ?? "";
                if (string.Equals(existingTo, toIdHex, StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.Equals(existingTo, "0xffffffff", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(toIdHex, "0xffffffff", StringComparison.OrdinalIgnoreCase))
                {
                    AppState.Messages.Remove(existingByPacket);
                }
                else
                {
                    return;
                }
            }
        }

        if (AppState.Messages.Any(x =>
                !x.IsMine &&
                string.Equals(x.FromIdHex, fromIdHex, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ToIdHex, toIdHex, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Text, text, StringComparison.Ordinal) &&
                (DateTime.UtcNow - x.WhenUtc).TotalMinutes <= 10))
        {
            return;
        }

        var fromNode = AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, fromIdHex, StringComparison.OrdinalIgnoreCase));
        var fromName = fromNode?.Name ?? fromIdHex;

        var msg = new MessageLive
        {
            FromIdHex = fromIdHex,
            FromName = fromName,
            ToIdHex = toIdHex,
            ToName = toName,
            Text = text,
            When = DateTime.Now.ToString("HH:mm:ss"),
            WhenUtc = DateTime.UtcNow,
            IsMine = false,
            PacketId = packetId,
            IsHeard = false,
            IsDelivered = false,
            DmTargetNodeNum = 0
        };

        AppState.NotifyIncomingMessage(msg.IsDirect ? fromIdHex : null, msg.WhenUtc);
        AppState.Messages.Insert(0, msg);
        while (AppState.Messages.Count > MaxMessages)
            AppState.Messages.RemoveAt(AppState.Messages.Count - 1);

        if (msg.IsDirect)
            MessageArchive.Append(msg, dmPeerIdHex: fromIdHex);
        else
            MessageArchive.Append(msg, channelName: "Primary");
    }

    private static string NormalizeNodeId(string rawHex)
    {
        var hex = (rawHex ?? string.Empty).Trim();
        if (hex.Length >= 8)
            return hex.ToLowerInvariant();

        return hex.PadLeft(8, '0').ToLowerInvariant();
    }

    private static bool TryGetToNodeForPacket(uint packetId, out uint toNode)
    {
        lock (PacketMetaLock)
        {
            TrimPacketMeta(DateTime.UtcNow);
            if (TextPacketMetaById.TryGetValue(packetId, out var meta))
            {
                toNode = meta.ToNode;
                return true;
            }
        }

        toNode = 0xFFFFFFFF;
        return false;
    }

    private static void TrimPacketMeta(DateTime nowUtc)
    {
        foreach (var key in TextPacketMetaById.Where(kvp => (nowUtc - kvp.Value.SeenUtc) > PacketMetaTtl).Select(kvp => kvp.Key).ToList())
            TextPacketMetaById.Remove(key);

        if (TextPacketMetaById.Count <= MaxPacketMetaEntries)
            return;

        foreach (var key in TextPacketMetaById.OrderBy(kvp => kvp.Value.SeenUtc).Take(TextPacketMetaById.Count - MaxPacketMetaEntries).Select(kvp => kvp.Key).ToList())
            TextPacketMetaById.Remove(key);
    }
}
