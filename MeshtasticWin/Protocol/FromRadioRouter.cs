using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeshtasticWin.Services;
using MeshtasticWin.Models;
using Meshtastic.Protobufs;

namespace MeshtasticWin.Protocol;

public static class FromRadioRouter
{
    private const int TextMessagePortNum = 1; // TEXT_MESSAGE_APP
    private const int TextMessageCompressedPortNum = 7; // TEXT_MESSAGE_COMPRESSED_APP
    private const int PositionPortNum = 3;    // POSITION_APP
    private const int RoutingAppPortNum = 5;  // ROUTING_APP
    private const int DetectionSensorPortNum = 10; // DETECTION_SENSOR_APP
    private const int TelemetryAppPortNum = 67; // TELEMETRY_APP
    private const int TraceRoutePortNum = 70; // TRACEROUTE_APP
    private const int MaxMessages = 500;
    private static readonly DateTime MinTelemetryTimestampUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, string> HardwareModelFriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TRACKER_T1000_E"] = "Seeed Card Tracker T1000-E",
        ["T_ECHO"] = "LilyGO T-Echo",
        ["T_ECHO_PLUS"] = "LilyGO T-Echo Plus",
        ["HELTEC_MESH_NODE_T114"] = "Heltec Mesh Node T114",
        ["RAK4631"] = "RAK WisBlock 4631",
        ["HELTEC_V3"] = "Heltec V3",
        ["WIO_WM1110"] = "Seeed Wio WM1110 Tracker",
        ["WISMESH_TAP"] = "RAK WisMesh Tap",
        ["HELTEC_WIRELESS_TRACKER"] = "Heltec Wireless Tracker",
        ["HELTEC_WIRELESS_TRACKER_V1_0"] = "Heltec Wireless Tracker V1.0",
        ["WIO_E5"] = "Seeed Wio-E5"
    };

    private static readonly Dictionary<int, string> HardwareModelFriendlyNamesById = new()
    {
        [71] = "Seeed Card Tracker T1000-E",
        [7] = "LilyGO T-Echo",
        [69] = "Heltec Mesh Node T114",
        [9] = "RAK WisBlock 4631",
        [43] = "Heltec V3"
    };

    private static readonly Lazy<Dictionary<int, string>> HardwareModelFriendlyNamesFromEnumById =
        new(BuildHardwareModelFriendlyNamesFromEnumById);

    public static bool TryHandle(byte[] payload, Action<Action> runOnUi, Action<string> logToUi, out string summary)
        => TryApplyFromRadio(payload, runOnUi, logToUi, out summary);

    public static bool TryApplyFromRadio(byte[] frame, Action<Action> runOnUi, Action<string> logToUi, out string summary)
    {
        summary = "unknown";

        try
        {
            var fromRadio = FromRadio.Parser.ParseFrom(frame);
            switch (fromRadio.PayloadVariantCase)
            {
                case FromRadio.PayloadVariantOneofCase.Packet:
                    summary = "Packet";
                    runOnUi(() => ApplyPacketObject(fromRadio.Packet, logToUi));
                    return true;

                case FromRadio.PayloadVariantOneofCase.NodeInfo:
                    summary = "NodeInfo";
                    runOnUi(() => ApplyNodeInfoObject(fromRadio.NodeInfo, logToUi));
                    return true;

                case FromRadio.PayloadVariantOneofCase.MyInfo:
                    summary = "MyInfo";
                    runOnUi(() => ApplyMyInfoObject(fromRadio.MyInfo));
                    return true;

                case FromRadio.PayloadVariantOneofCase.Metadata:
                    summary = "DeviceMetadata";
                    runOnUi(() => ApplyDeviceMetadataObject(fromRadio.Metadata));
                    return true;

                case FromRadio.PayloadVariantOneofCase.QueueStatus:
                    summary = "QueueStatus";
                    runOnUi(() => ApplyQueueStatusObject(fromRadio.QueueStatus, logToUi));
                    return true;

                case FromRadio.PayloadVariantOneofCase.LogRecord:
                    summary = BuildReadableVariantSummary("LogRecord", fromRadio.LogRecord);
                    return true;

                case FromRadio.PayloadVariantOneofCase.None:
                    summary = "other";
                    return true;

                default:
                    summary = fromRadio.PayloadVariantCase.ToString();
                    return true;
            }
        }
        catch (Exception ex)
        {
            summary = $"not FromRadio ({ex.GetType().Name})";
            return false;
        }
    }

    private static void ApplyPacketObject(object packetObj, Action<string> logToUi)
    {
        if (!TryGetUInt(packetObj, "From", out var fromNodeNum))
            return;

        uint packetId = 0;
        TryGetUInt(packetObj, "Id", out packetId);

        uint toNodeNum = 0xFFFFFFFF;
        TryGetUInt(packetObj, "To", out toNodeNum);

        var fromIdHex = $"0x{fromNodeNum:x8}";
        var toIdHex = $"0x{toNodeNum:x8}";

        // Ensure node object exists so SNR/RSSI can be updated before NodeInfo arrives.
        var fromNode = EnsureNode(fromIdHex, fromNodeNum);
        fromNode.Touch();

        // SNR/RSSI field names can vary, so try multiple names.
        double? rxSnr = null;
        if (TryGetDouble(packetObj, "RxSnr", out var snr) || TryGetDouble(packetObj, "Snr", out snr))
        {
            fromNode.SNR = snr.ToString("0.0");
            rxSnr = snr;
        }

        double? rxRssi = null;
        if (TryGetDouble(packetObj, "RxRssi", out var rssi) || TryGetDouble(packetObj, "Rssi", out rssi))
        {
            fromNode.RSSI = rssi.ToString("0");
            rxRssi = rssi;
        }

        var decodedObj = packetObj.GetType()
            .GetProperty("Decoded", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(packetObj);

        if (decodedObj is null)
            return;

        var portNum = TryGetPortNum(decodedObj);

        uint? channelIndex = null;
        if (TryGetUInt(packetObj, "Channel", out var channelValue))
            channelIndex = channelValue;

        // --- ACK / ROUTING_APP ---
        if (portNum == RoutingAppPortNum)
        {
            if (TryHandleRoutingTraceRouteFromPayload(decodedObj, fromNodeNum, toNodeNum, channelIndex, rxSnr, rxRssi, logToUi, out var routingSummary))
                logToUi("TraceRoute (passive): " + routingSummary);
            TryHandleAck(decodedObj, fromNodeNum, logToUi);
            return;
        }

        // --- GPS / POSITION_APP ---
        if (portNum == PositionPortNum)
        {
            if (TryHandlePositionFromPayload(decodedObj, fromNodeNum, source: "position_packet", logToUi, out var s))
                logToUi("GPS: " + s);
            return;
        }

        // --- TELEMETRY_APP ---
        if (portNum == TelemetryAppPortNum)
        {
            if (TryHandleTelemetryFromPayload(decodedObj, fromNodeNum, logToUi, out var s))
                logToUi("Telemetry: " + s);
            return;
        }

        // --- TRACEROUTE_APP ---
        if (portNum == TraceRoutePortNum)
        {
            if (TryHandleTraceRouteFromPayload(decodedObj, fromNodeNum, toNodeNum, rxSnr, rxRssi, logToUi, out var s))
                logToUi("TraceRoute: " + s);
            return;
        }

        // --- DETECTION_SENSOR_APP ---
        if (portNum == DetectionSensorPortNum)
        {
            if (TryHandleDetectionSensorFromPayload(decodedObj, fromNodeNum, logToUi, out var s))
                logToUi("Detection: " + s);
            return;
        }

        // --- TEXT_MESSAGE_APP / TEXT_MESSAGE_COMPRESSED_APP ---
        if (portNum != TextMessagePortNum && portNum != TextMessageCompressedPortNum)
            return;

        if (fromNodeNum == 0)
            return;

        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return;

        if (!TryDecodeIncomingText(portNum, payloadBytes, out var text))
            return;

        if (packetId != 0)
        {
            var existingByPacket = MeshtasticWin.AppState.Messages.FirstOrDefault(m => !m.IsMine && m.PacketId == packetId);
            if (existingByPacket is not null)
            {
                var existingTo = existingByPacket.ToIdHex ?? "";
                if (string.Equals(existingTo, toIdHex, StringComparison.OrdinalIgnoreCase))
                {
                    if (ShouldReplaceDegradedText(existingByPacket.Text, text))
                        ReplaceIncomingMessageText(existingByPacket, text, fromNode.Name, toNodeNum, toIdHex);
                    return;
                }

                if (ShouldReplaceDegradedText(existingByPacket.Text, text))
                {
                    ReplaceIncomingMessageText(existingByPacket, text, fromNode.Name, toNodeNum, toIdHex);
                    return;
                }

                if (string.Equals(existingTo, "0xffffffff", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(toIdHex, "0xffffffff", StringComparison.OrdinalIgnoreCase))
                {
                    MeshtasticWin.AppState.Messages.Remove(existingByPacket);
                }
                else
                {
                    return;
                }
            }
        }
        else if (TryReplaceRecentDegradedFallback(fromIdHex, toIdHex, text, fromNode.Name, toNodeNum))
        {
            return;
        }

        if (MeshtasticWin.AppState.Messages.Any(m =>
                !m.IsMine &&
                string.Equals(m.FromIdHex, fromIdHex, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.ToIdHex, toIdHex, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Text, text, StringComparison.Ordinal) &&
                (DateTime.UtcNow - m.WhenUtc).TotalMinutes <= 10))
        {
            return;
        }

        var toNode = MeshtasticWin.AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, toIdHex, StringComparison.OrdinalIgnoreCase));
        var toName = (toNodeNum == 0xFFFFFFFF) ? "Primary" : (toNode?.Name ?? toIdHex);
        var msg = new MessageLive
        {
            FromIdHex = fromIdHex,
            FromName = fromNode.Name,
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

        // Update unread indicators
        MeshtasticWin.AppState.NotifyIncomingMessage(msg.IsDirect ? fromIdHex : null, msg.WhenUtc);

        MeshtasticWin.AppState.Messages.Insert(0, msg);
        while (MeshtasticWin.AppState.Messages.Count > MaxMessages)
            MeshtasticWin.AppState.Messages.RemoveAt(MeshtasticWin.AppState.Messages.Count - 1);

        if (msg.IsDirect)
            MessageArchive.Append(msg, dmPeerIdHex: fromIdHex);
        else
            MessageArchive.Append(msg, channelName: "Primary");
    }

    private static bool TryDecodeIncomingText(int portNum, byte[] payloadBytes, out string text)
    {
        text = "";

        if (portNum == TextMessageCompressedPortNum &&
            TryDecodeCompressedTextPayload(payloadBytes, out var compressedText))
        {
            text = compressedText;
            return true;
        }

        try
        {
            text = System.Text.Encoding.UTF8.GetString(payloadBytes).Trim('\0', '\r', '\n');
        }
        catch
        {
            text = "";
            return false;
        }

        return !string.IsNullOrWhiteSpace(text) && LooksLikeHumanReadableText(text);
    }

    private static bool TryDecodeCompressedTextPayload(byte[] payloadBytes, out string text)
    {
        text = "";

        Compressed compressedObj;
        try
        {
            compressedObj = Compressed.Parser.ParseFrom(payloadBytes);
        }
        catch
        {
            return false;
        }

        var wrappedPortNum = (int)compressedObj.Portnum;
        if (wrappedPortNum != 0 && wrappedPortNum != TextMessagePortNum)
            return false;

        var wrappedData = compressedObj.Data?.ToByteArray() ?? Array.Empty<byte>();
        if (wrappedData.Length == 0)
            return false;

        try
        {
            text = System.Text.Encoding.UTF8.GetString(wrappedData).Trim('\0', '\r', '\n');
        }
        catch
        {
            text = "";
            return false;
        }

        return !string.IsNullOrWhiteSpace(text) && LooksLikeHumanReadableText(text);
    }

    private static bool LooksLikeHumanReadableText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var ch in text)
        {
            if (char.IsControl(ch) && !char.IsWhiteSpace(ch))
                return false;
        }

        return true;
    }

    private static bool ShouldReplaceDegradedText(string? existingText, string incomingText)
    {
        if (string.IsNullOrWhiteSpace(incomingText))
            return false;

        if (string.IsNullOrWhiteSpace(existingText))
            return true;

        if (string.Equals(existingText, incomingText, StringComparison.Ordinal))
            return false;

        return LooksLikePlaceholderDebugText(existingText) && !LooksLikePlaceholderDebugText(incomingText);
    }

    private static bool LooksLikePlaceholderDebugText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var hashCount = 0;
        var longestHashRun = 0;
        var currentRun = 0;
        foreach (var ch in text)
        {
            if (ch == '#')
            {
                hashCount++;
                currentRun++;
                if (currentRun > longestHashRun)
                    longestHashRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }

        var ratio = text.Length == 0 ? 0.0 : (double)hashCount / text.Length;
        return longestHashRun >= 3 || ratio >= 0.2;
    }

    private static void ReplaceIncomingMessageText(MessageLive existing, string text, string fromName, uint toNodeNum, string toIdHex)
    {
        var index = MeshtasticWin.AppState.Messages.IndexOf(existing);
        if (index < 0)
            return;

        var toNode = MeshtasticWin.AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, toIdHex, StringComparison.OrdinalIgnoreCase));
        var toName = toNodeNum == 0xFFFFFFFF ? "Primary" : (toNode?.Name ?? toIdHex);

        var updated = new MessageLive
        {
            FromIdHex = existing.FromIdHex,
            FromName = string.IsNullOrWhiteSpace(fromName) ? existing.FromName : fromName,
            ToIdHex = toIdHex,
            ToName = toName,
            Text = text,
            When = existing.When,
            WhenUtc = existing.WhenUtc,
            IsMine = false,
            PacketId = existing.PacketId,
            IsHeard = existing.IsHeard,
            IsDelivered = existing.IsDelivered,
            DmTargetNodeNum = existing.DmTargetNodeNum
        };

        MeshtasticWin.AppState.Messages[index] = updated;
    }

    private static bool TryReplaceRecentDegradedFallback(string fromIdHex, string toIdHex, string incomingText, string fromName, uint toNodeNum)
    {
        if (string.IsNullOrWhiteSpace(incomingText))
            return false;

        var nowUtc = DateTime.UtcNow;
        var candidate = MeshtasticWin.AppState.Messages
            .Where(m =>
                !m.IsMine &&
                m.PacketId == 0 &&
                string.Equals(m.FromIdHex, fromIdHex, StringComparison.OrdinalIgnoreCase) &&
                (nowUtc - m.WhenUtc).TotalSeconds <= 20 &&
                IsPotentialSameDestination(m.ToIdHex ?? "", toIdHex))
            .OrderBy(m => Math.Abs((nowUtc - m.WhenUtc).TotalSeconds))
            .FirstOrDefault();

        if (candidate is null)
            return false;

        if (string.Equals(candidate.Text, incomingText, StringComparison.Ordinal))
            return true;

        if (!ShouldReplaceDegradedText(candidate.Text, incomingText))
            return false;

        ReplaceIncomingMessageText(candidate, incomingText, fromName, toNodeNum, toIdHex);
        return true;
    }

    private static bool IsPotentialSameDestination(string existingToIdHex, string incomingToIdHex)
    {
        if (string.Equals(existingToIdHex, incomingToIdHex, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(existingToIdHex, "0xffffffff", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(incomingToIdHex, "0xffffffff", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyNodeInfoObject(object nodeInfoObj, Action<string> logToUi)
    {
        if (!TryGetUInt(nodeInfoObj, "Num", out var nodeNum) && !TryGetUInt(nodeInfoObj, "NodeNum", out nodeNum))
            return;

        var idHex = $"0x{nodeNum:x8}";
        var node = EnsureNode(idHex, nodeNum);

        // User
        var userObj = nodeInfoObj.GetType().GetProperty("User", BindingFlags.Public | BindingFlags.Instance)?.GetValue(nodeInfoObj);
        if (userObj is not null)
        {
            var longName = userObj.GetType().GetProperty("LongName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(userObj)?.ToString() ?? "";
            var shortName = userObj.GetType().GetProperty("ShortName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(userObj)?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(longName))
                node.LongName = longName;

            if (!string.IsNullOrWhiteSpace(shortName))
                node.ShortName = shortName;

            if (TryGetString(userObj, "Id", out var userId))
                node.UserId = userId;

            if (TryGetBytes(userObj, "PublicKey", out var publicKeyBytes))
                node.PublicKey = Convert.ToHexString(publicKeyBytes);

            if (TryGetObj(userObj, "Role", out var roleObj))
            {
                var role = ResolveRoleText(roleObj);
                if (!string.IsNullOrWhiteSpace(role))
                    node.Role = role;
            }

            if (TryGetObj(userObj, "HwModel", out var hwObj))
            {
                var hardwareModel = ResolveHardwareModelText(hwObj);
                if (!string.IsNullOrWhiteSpace(hardwareModel))
                    node.HardwareModel = hardwareModel;
            }
        }

        if (TryGetUInt(nodeInfoObj, "LastHeard", out var lastHeardSeconds) && lastHeardSeconds > 0)
        {
            var lastHeardUtc = DateTimeOffset.FromUnixTimeSeconds(lastHeardSeconds).UtcDateTime;
            node.SetFirstHeard(lastHeardUtc);
        }

        if (TryGetObj(nodeInfoObj, "DeviceMetrics", out var deviceMetricsObj) &&
            deviceMetricsObj is not null &&
            TryGetUInt(deviceMetricsObj, "UptimeSeconds", out var uptimeSeconds) &&
            uptimeSeconds > 0)
        {
            node.UptimeSeconds = uptimeSeconds;
        }

        if (RadioClient.Instance.IsConnected)
        {
            var connectedId = MeshtasticWin.AppState.ConnectedNodeIdHex;
            if (string.IsNullOrWhiteSpace(connectedId) ||
                string.Equals(connectedId, idHex, StringComparison.OrdinalIgnoreCase))
            {
                MeshtasticWin.AppState.SetConnectedNodeIdHex(idHex);
            }
        }

        // Bootstrap GPS from NodeInfo if present.
        object? posObj =
            nodeInfoObj.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance)?.GetValue(nodeInfoObj) ??
            nodeInfoObj.GetType().GetProperty("LastPosition", BindingFlags.Public | BindingFlags.Instance)?.GetValue(nodeInfoObj);

        if (posObj is not null)
        {
            if (TryHandlePositionFromNodeInfoPositionObject(posObj, nodeNum, "nodeinfo_bootstrap", logToUi, out var s))
                logToUi("GPS: " + s);
        }

        node.Touch();
    }

    private static void ApplyMyInfoObject(object myInfoObj)
    {
        if (!TryGetUInt(myInfoObj, "MyNodeNum", out var nodeNum) || nodeNum == 0)
            return;

        var idHex = $"0x{nodeNum:x8}";
        var node = EnsureNode(idHex, nodeNum);
        node.Touch();
        MeshtasticWin.AppState.SetConnectedNodeIdHex(idHex);
    }

    private static void ApplyDeviceMetadataObject(object metadataObj)
    {
        var connectedId = MeshtasticWin.AppState.ConnectedNodeIdHex;
        var node = !string.IsNullOrWhiteSpace(connectedId)
            ? MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, connectedId, StringComparison.OrdinalIgnoreCase))
            : null;

        if (node is null && MeshtasticWin.AppState.Nodes.Count == 1)
            node = MeshtasticWin.AppState.Nodes[0];

        if (node is null)
            return;

        if (TryGetString(metadataObj, "FirmwareVersion", out var firmwareVersion))
            node.FirmwareVersion = firmwareVersion;

        if (TryGetObj(metadataObj, "Role", out var roleObj))
        {
            var role = ResolveRoleText(roleObj);
            if (!string.IsNullOrWhiteSpace(role))
                node.Role = role;
        }

        if (TryGetObj(metadataObj, "HwModel", out var hwObj))
        {
            var hardwareModel = ResolveHardwareModelText(hwObj);
            if (!string.IsNullOrWhiteSpace(hardwareModel))
                node.HardwareModel = hardwareModel;
        }
    }

    private static void ApplyQueueStatusObject(QueueStatus queueStatusObj, Action<string> logToUi)
    {
        var free = unchecked((int)queueStatusObj.Free);
        var maxLen = unchecked((int)queueStatusObj.Maxlen);
        var res = queueStatusObj.Res;
        var meshPacketId = queueStatusObj.MeshPacketId;

        RadioClient.Instance.ApplyQueueStatus(free, maxLen, res, meshPacketId);

        if (res != 0)
        {
            var freeText = free.ToString(CultureInfo.InvariantCulture);
            var maxText = maxLen.ToString(CultureInfo.InvariantCulture);
            logToUi($"QueueStatus error: res={res} free={freeText}/{maxText} id=0x{meshPacketId:x8}");
        }
    }
    private static NodeLive EnsureNode(string idHex, uint nodeNum)
    {
        var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, idHex, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            node = new NodeLive(idHex);
            MeshtasticWin.AppState.Nodes.Insert(0, node);
        }

        // Set NodeNum when the property exists.
        try { node.NodeNum = nodeNum; } catch { }

        return node;
    }

    private static void TryHandleAck(object decodedObj, uint ackFromNodeNum, Action<string> logToUi)
    {
        uint requestId = 0;

        var reqProp =
            decodedObj.GetType().GetProperty("RequestId", BindingFlags.Public | BindingFlags.Instance) ??
            decodedObj.GetType().GetProperty("ReplyId", BindingFlags.Public | BindingFlags.Instance) ??
            decodedObj.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

        if (reqProp is not null)
        {
            var v = reqProp.GetValue(decodedObj);
            if (v is uint u) requestId = u;
            else if (v is int i) requestId = unchecked((uint)i);
            else if (v is long l) requestId = unchecked((uint)l);
            else if (v is ulong ul) requestId = unchecked((uint)ul);
        }

        if (requestId == 0)
            return;

        for (int idx = 0; idx < MeshtasticWin.AppState.Messages.Count; idx++)
        {
            var m = MeshtasticWin.AppState.Messages[idx];
            if (!m.IsMine) continue;
            if (m.PacketId != requestId) continue;

            MeshtasticWin.AppState.Messages[idx] = m.WithAckFrom(ackFromNodeNum);
            return;
        }

        logToUi($"ACK (unknown request_id=0x{requestId:x8})");
    }

    // GPS from packet.Payload (protobuf Position)
    private static bool TryHandlePositionFromPayload(object decodedObj, uint fromNodeNum, string source, Action<string> logToUi, out string summary)
    {
        summary = "payload empty";
        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return false;

        return TryParsePositionBytesAndApply(payloadBytes, fromNodeNum, source, logToUi, out summary);
    }

    // GPS from NodeInfo.Position / LastPosition (protobuf Position object)
    private static bool TryHandlePositionFromNodeInfoPositionObject(object posObj, uint fromNodeNum, string source, Action<string> logToUi, out string summary)
    {
        summary = "missing LatitudeI/LongitudeI";

        if (!TryGetInt(posObj, "LatitudeI", out var latI) || !TryGetInt(posObj, "LongitudeI", out var lonI))
            return false;

        double lat = latI / 1e7;
        double lon = lonI / 1e7;

        double? alt = null;
        if (TryGetInt(posObj, "Altitude", out var altI))
            alt = altI;

        DateTime tsUtc = DateTime.UtcNow;
        if (TryGetUInt(posObj, "Time", out var timeSec) && timeSec > 0)
            tsUtc = DateTimeOffset.FromUnixTimeSeconds(timeSec).UtcDateTime;
        else if (TryGetUInt(posObj, "Timestamp", out var tsSec) && tsSec > 0)
            tsUtc = DateTimeOffset.FromUnixTimeSeconds(tsSec).UtcDateTime;

        ApplyGpsToNode(fromNodeNum, lat, lon, tsUtc, alt, source, logToUi, out summary);
        return true;
    }

    private static bool TryParsePositionBytesAndApply(byte[] payloadBytes, uint fromNodeNum, string source, Action<string> logToUi, out string summary)
    {
        Position posObj;
        try
        {
            posObj = Position.Parser.ParseFrom(payloadBytes);
        }
        catch (Exception ex)
        {
            summary = $"Position.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (!posObj.HasLatitudeI || !posObj.HasLongitudeI)
        {
            summary = "missing LatitudeI/LongitudeI";
            return false;
        }

        double lat = posObj.LatitudeI / 1e7;
        double lon = posObj.LongitudeI / 1e7;

        double? alt = posObj.HasAltitude ? posObj.Altitude : null;

        DateTime tsUtc = DateTime.UtcNow;
        if (posObj.Time > 0)
            tsUtc = DateTimeOffset.FromUnixTimeSeconds(posObj.Time).UtcDateTime;
        else if (posObj.Timestamp > 0)
            tsUtc = DateTimeOffset.FromUnixTimeSeconds(posObj.Timestamp).UtcDateTime;

        ApplyGpsToNode(fromNodeNum, lat, lon, tsUtc, alt, source, logToUi, out summary);
        return true;
    }

    private static void ApplyGpsToNode(uint fromNodeNum, double lat, double lon, DateTime tsUtc, double? alt, string source, Action<string> logToUi, out string summary)
    {
        var fromIdHex = $"0x{fromNodeNum:x8}";
        var node = EnsureNode(fromIdHex, fromNodeNum);

        node.UpdatePosition(lat, lon, tsUtc, alt);

        try
        {
            // If GpsArchive has a different signature in your branch, adjust this call.
            GpsArchive.Append(node.IdHex, lat, lon, tsUtc, alt, src: source);
        }
        catch (Exception ex)
        {
            logToUi($"GPS log write failed: {ex.Message}");
        }

        node.Touch();
        summary = $"{node.ShortId} {lat:0.000000},{lon:0.000000} ({tsUtc:HH:mm:ss}Z) src={source}";
    }

    private static bool TryHandleTelemetryFromPayload(object decodedObj, uint fromNodeNum, Action<string> logToUi, out string summary)
    {
        summary = "telemetry empty";
        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return false;

        Telemetry telemetryObj;
        try
        {
            telemetryObj = Telemetry.Parser.ParseFrom(payloadBytes);
        }
        catch (Exception ex)
        {
            summary = $"Telemetry.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        var receiveTimeUtc = DateTime.UtcNow;
        var tsUtc = receiveTimeUtc;
        if (telemetryObj.Time > 0)
            tsUtc = NormalizeTelemetryTimestamp(telemetryObj.Time, receiveTimeUtc);

        var fromIdHex = $"0x{fromNodeNum:x8}";
        var logType = (NodeLogType?)null;
        var label = "";
        object? metricsObj = null;

        switch (telemetryObj.VariantCase)
        {
            case Telemetry.VariantOneofCase.DeviceMetrics:
                metricsObj = telemetryObj.DeviceMetrics;
                logType = NodeLogType.DeviceMetrics;
                label = "device";
                break;

            case Telemetry.VariantOneofCase.EnvironmentMetrics:
                metricsObj = telemetryObj.EnvironmentMetrics;
                logType = NodeLogType.EnvironmentMetrics;
                label = "environment";
                break;

            case Telemetry.VariantOneofCase.PowerMetrics:
                metricsObj = telemetryObj.PowerMetrics;
                logType = NodeLogType.PowerMetrics;
                label = "power";
                break;

            case Telemetry.VariantOneofCase.AirQualityMetrics:
                metricsObj = telemetryObj.AirQualityMetrics;
                logType = NodeLogType.EnvironmentMetrics;
                label = "air_quality";
                break;
        }

        if (logType is null || metricsObj is null)
        {
            summary = "Telemetry (unknown variant)";
            return true;
        }

        var formatted = FormatProtoSingleLine(metricsObj);
        var line = $"{label}: {formatted}";
        if (logType == NodeLogType.DeviceMetrics)
        {
            TryCaptureDeviceMetrics(metricsObj, fromIdHex, tsUtc, logToUi);
        }
        else
        {
            NodeLogArchive.Append(logType.Value, fromIdHex, tsUtc, line);
        }
        summary = line;
        return true;
    }

    private static DateTime NormalizeTelemetryTimestamp(uint timeSec, DateTime receiveTimeUtc)
    {
        if (timeSec == 0)
            return receiveTimeUtc;

        var tsUtc = DateTimeOffset.FromUnixTimeSeconds(timeSec).UtcDateTime;
        if (tsUtc < MinTelemetryTimestampUtc)
            return receiveTimeUtc;
        if (tsUtc > receiveTimeUtc.AddDays(1))
            return receiveTimeUtc;

        return tsUtc;
    }

    private static void TryCaptureDeviceMetrics(object metricsObj, string fromIdHex, DateTime tsUtc, Action<string> logToUi)
    {
        double? voltage = null;
        if (TryGetDouble(metricsObj, "Voltage", out var voltageVal))
            voltage = voltageVal;

        double? channelUtil = null;
        if (TryGetDouble(metricsObj, "ChannelUtilization", out var channelVal))
            channelUtil = channelVal;

        double? airtime = null;
        if (TryGetDouble(metricsObj, "AirUtilTx", out var airVal))
            airtime = airVal;

        double? batteryPercent = null;
        bool? isPowered = null;
        if (TryGetUInt(metricsObj, "BatteryLevel", out var batteryLevel))
        {
            if (batteryLevel <= 100)
                batteryPercent = batteryLevel;
            isPowered = batteryLevel > 100;
        }

        if (TryGetUInt(metricsObj, "UptimeSeconds", out var uptimeSeconds) && uptimeSeconds > 0)
        {
            var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                string.Equals(n.IdHex, fromIdHex, StringComparison.OrdinalIgnoreCase));
            if (node is not null)
                node.UptimeSeconds = uptimeSeconds;
        }

        var sample = new DeviceMetricSample(tsUtc, voltage, channelUtil, airtime, isPowered)
        {
            BatteryPercent = batteryPercent
        };

        try
        {
            DeviceMetricsLogService.AppendSample(fromIdHex, sample);
        }
        catch (Exception ex)
        {
            logToUi($"Device metrics log write failed: {ex.Message}");
        }
    }

    private static bool TryHandleTraceRouteFromPayload(
        object decodedObj,
        uint fromNodeNum,
        uint toNodeNum,
        double? rxSnr,
        double? rxRssi,
        Action<string> logToUi,
        out string summary)
    {
        summary = "traceroute empty";
        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return false;

        RouteDiscovery routeObj;
        try
        {
            routeObj = RouteDiscovery.Parser.ParseFrom(payloadBytes);
        }
        catch (Exception ex)
        {
            summary = $"RouteDiscovery.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        var formatted = FormatProtoSingleLine(routeObj);
        var sb = new StringBuilder();
        if (TraceRouteContext.TryMatchActiveTraceRoute(fromNodeNum, out var match))
        {
            sb.Append("active: true ");
            if (toNodeNum != 0xFFFFFFFF)
                sb.Append("from: ").Append(toNodeNum).Append(' ');
            sb.Append("to: ").Append(match.TargetNodeNum).Append(' ');
        }
        else
        {
            sb.Append("passive: true ");
            sb.Append("from: ").Append(fromNodeNum).Append(' ');
            if (toNodeNum != 0xFFFFFFFF)
            {
                sb.Append("to: ").Append(toNodeNum).Append(' ');
            }
            else
            {
                var connected = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                    string.Equals(n.IdHex, MeshtasticWin.AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));
                if (connected?.NodeNum > 0)
                    sb.Append("to: ").Append(connected.NodeNum).Append(' ');
            }
        }

        sb.Append(formatted);
        formatted = sb.ToString();
        if (rxSnr.HasValue || rxRssi.HasValue)
        {
            sb = new StringBuilder(formatted);
            if (rxSnr.HasValue)
                sb.Append(" rx_snr: ").Append(rxSnr.Value.ToString("0.0", CultureInfo.InvariantCulture));
            if (rxRssi.HasValue)
                sb.Append(" rx_rssi: ").Append(rxRssi.Value.ToString("0.0", CultureInfo.InvariantCulture));
            formatted = sb.ToString();
        }
        var fromIdHex = $"0x{fromNodeNum:x8}";
        NodeLogArchive.Append(NodeLogType.TraceRoute, fromIdHex, DateTime.UtcNow, formatted);
        summary = formatted;
        return true;
    }

    private static bool TryHandleRoutingTraceRouteFromPayload(object decodedObj, uint fromNodeNum, uint toNodeNum, uint? channelIndex, double? rxSnr, double? rxRssi, Action<string> logToUi, out string summary)
    {
        summary = "routing empty";
        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return false;

        Routing routingObj;
        try
        {
            routingObj = Routing.Parser.ParseFrom(payloadBytes);
        }
        catch (Exception ex)
        {
            summary = $"Routing.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        object? routeObj = null;
        var variant = "";
        switch (routingObj.VariantCase)
        {
            case Routing.VariantOneofCase.RouteRequest:
                routeObj = routingObj.RouteRequest;
                variant = "route_request";
                break;

            case Routing.VariantOneofCase.RouteReply:
                routeObj = routingObj.RouteReply;
                variant = "route_reply";
                break;
        }

        if (routeObj is null)
            return false;

        var formatted = FormatProtoSingleLine(routeObj);
        var sb = new StringBuilder();
        var isActive = TraceRouteContext.TryMatchActiveTraceRoute(fromNodeNum, out var match);
        if (isActive)
        {
            sb.Append("active: true ");
            if (!string.IsNullOrWhiteSpace(variant))
                sb.Append("variant: ").Append(variant).Append(' ');
            if (toNodeNum != 0xFFFFFFFF)
                sb.Append("from: ").Append(toNodeNum).Append(' ');
            sb.Append("to: ").Append(match.TargetNodeNum).Append(' ');
        }
        else
        {
            sb.Append("passive: true ");
            if (!string.IsNullOrWhiteSpace(variant))
                sb.Append("variant: ").Append(variant).Append(' ');
            sb.Append("from: ").Append(fromNodeNum).Append(' ');
            if (toNodeNum != 0xFFFFFFFF)
            {
                sb.Append("to: ").Append(toNodeNum).Append(' ');
            }
            else
            {
                var connected = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
                    string.Equals(n.IdHex, MeshtasticWin.AppState.ConnectedNodeIdHex, StringComparison.OrdinalIgnoreCase));
                if (connected?.NodeNum > 0)
                    sb.Append("to: ").Append(connected.NodeNum).Append(' ');
            }
        }
        if (channelIndex.HasValue)
            sb.Append("channel: ").Append(channelIndex.Value).Append(' ');
        sb.Append(formatted);

        if (rxSnr.HasValue)
            sb.Append(" rx_snr: ").Append(rxSnr.Value.ToString("0.0", CultureInfo.InvariantCulture));
        if (rxRssi.HasValue)
            sb.Append(" rx_rssi: ").Append(rxRssi.Value.ToString("0.0", CultureInfo.InvariantCulture));

        var summaryText = sb.ToString().Trim();
        var fromIdHex = $"0x{fromNodeNum:x8}";
        NodeLogArchive.Append(NodeLogType.TraceRoute, fromIdHex, DateTime.UtcNow, summaryText);
        summary = summaryText;
        return true;
    }

    private static bool TryHandleDetectionSensorFromPayload(object decodedObj, uint fromNodeNum, Action<string> logToUi, out string summary)
    {
        summary = "detection empty";
        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return false;

        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(payloadBytes).Trim('\0', '\r', '\n');
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var fromIdHex = $"0x{fromNodeNum:x8}";
        NodeLogArchive.Append(NodeLogType.DetectionSensor, fromIdHex, DateTime.UtcNow, text);
        summary = text;
        return true;
    }

    private static int TryGetPortNum(object decodedObj)
    {
        var p = decodedObj.GetType().GetProperty("Portnum", BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return -1;

        return ConvertToInt(p.GetValue(decodedObj));
    }

    private static int ConvertToInt(object? value)
    {
        if (value is null) return -1;
        if (value.GetType().IsEnum) return (int)value;
        if (value is int i) return i;
        if (value is uint u) return (int)u;
        if (value is long l && l <= int.MaxValue && l >= int.MinValue) return (int)l;
        if (value is ulong ul && ul <= int.MaxValue) return (int)ul;
        return -1;
    }

    private static byte[]? TryGetPayloadBytes(object decodedObj)
    {
        var p = decodedObj.GetType().GetProperty("Payload", BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return null;

        var v = p.GetValue(decodedObj);
        if (v is null) return null;

        var toBytes = v.GetType().GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Instance);
        if (toBytes is not null)
        {
            var res = toBytes.Invoke(v, Array.Empty<object>());
            if (res is byte[] b) return b;
        }

        return null;
    }

    private static bool TryGetUInt(object obj, string propName, out uint value)
    {
        value = 0;
        var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return false;

        var v = p.GetValue(obj);
        if (v is uint u) { value = u; return true; }
        if (v is int i && i >= 0) { value = (uint)i; return true; }
        return false;
    }

    private static bool TryGetString(object obj, string propName, out string value)
    {
        value = "";
        var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return false;

        var v = p.GetValue(obj);
        if (v is null) return false;

        value = (v.ToString() ?? "").Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBytes(object obj, string propName, out byte[] value)
    {
        value = Array.Empty<byte>();
        var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return false;

        var v = p.GetValue(obj);
        if (v is null) return false;

        if (v is byte[] bytes)
        {
            if (bytes.Length == 0) return false;
            value = bytes;
            return true;
        }

        var toByteArray = v.GetType().GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Instance, Type.DefaultBinder, Type.EmptyTypes, null);
        if (toByteArray?.Invoke(v, Array.Empty<object>()) is byte[] converted && converted.Length > 0)
        {
            value = converted;
            return true;
        }

        return false;
    }

    private static bool TryGetObj(object obj, string propName, out object? value)
    {
        value = null;
        var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return false;

        value = p.GetValue(obj);
        return value is not null;
    }

    private static bool TryGetInt(object obj, string propName, out int value)
    {
        value = 0;
        var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return false;

        var v = p.GetValue(obj);
        if (v is int i) { value = i; return true; }
        if (v is uint u && u <= int.MaxValue) { value = (int)u; return true; }
        return false;
    }

    private static bool TryGetDouble(object obj, string propName, out double value)
    {
        value = 0;
        var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return false;

        var v = p.GetValue(obj);
        if (v is null) return false;

        if (v is double d) { value = d; return true; }
        if (v is float f) { value = f; return true; }
        if (v is int i) { value = i; return true; }
        if (v is uint u) { value = u; return true; }
        if (v is long l) { value = l; return true; }
        if (double.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }

        return false;
    }

    private static string ResolveRoleText(object? roleObj)
    {
        if (roleObj is null)
            return "";

        var text = (roleObj.ToString() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = NormalizeToken(text);
        if (normalized is "unset" or "unknown" or "none")
            return "";

        return HumanizeToken(text);
    }

    private static string ResolveHardwareModelText(object? hwObj)
    {
        if (hwObj is null)
            return "";

        if (hwObj.GetType().IsEnum)
        {
            if (TryGetEnumNumericValue(hwObj, out var enumNumeric))
            {
                var mappedFromNumeric = ResolveHardwareModelFromNumeric(enumNumeric);
                if (!string.IsNullOrWhiteSpace(mappedFromNumeric))
                    return mappedFromNumeric;
            }

            if (TryGetEnumOriginalName(hwObj, out var originalEnumName))
            {
                if (HardwareModelFriendlyNames.TryGetValue(originalEnumName, out var mappedFromName))
                    return mappedFromName;

                return HumanizeToken(originalEnumName);
            }
        }

        if (hwObj is int i)
            return ResolveHardwareModelFromNumeric(i);
        if (hwObj is uint u)
            return ResolveHardwareModelFromNumeric((int)u);
        if (hwObj is long l && l <= int.MaxValue && l >= int.MinValue)
            return ResolveHardwareModelFromNumeric((int)l);
        if (hwObj is ulong ul && ul <= int.MaxValue)
            return ResolveHardwareModelFromNumeric((int)ul);

        var raw = (hwObj.ToString() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        if (HardwareModelFriendlyNames.TryGetValue(raw, out var mapped))
            return mapped;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            return ResolveHardwareModelFromNumeric(numeric);

        var normalized = NormalizeToken(raw);
        if (normalized is "unset" or "unknown" or "none")
            return "";

        return HumanizeToken(raw);
    }

    private static string ResolveHardwareModelFromNumeric(int numericValue)
    {
        if (numericValue <= 0)
            return "";

        if (HardwareModelFriendlyNamesById.TryGetValue(numericValue, out var mapped))
            return mapped;

        if (HardwareModelFriendlyNamesFromEnumById.Value.TryGetValue(numericValue, out var generatedMapped))
            return generatedMapped;

        return numericValue.ToString(CultureInfo.InvariantCulture);
    }

    private static string HumanizeToken(string raw)
    {
        var prepared = Regex.Replace(raw, "(?<=[a-z0-9])(?=[A-Z])", " ");
        prepared = Regex.Replace(prepared, "(?<=[A-Z])(?=[A-Z][a-z])", " ");

        var parts = prepared.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return raw;

        return string.Join(" ", parts.Select(static part =>
        {
            if (part.Length <= 3 || part.All(char.IsDigit))
                return part.ToUpperInvariant();

            return char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant();
        }));
    }

    private static Dictionary<int, string> BuildHardwareModelFriendlyNamesFromEnumById()
    {
        var result = new Dictionary<int, string>();
        var hardwareModelEnumType = typeof(HardwareModel);

        var values = Enum.GetValues(hardwareModelEnumType);
        foreach (var value in values)
        {
            if (value is null)
                continue;

            int numericValue;
            try { numericValue = Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { continue; }

            if (numericValue <= 0)
                continue;

            var enumName = value.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(enumName))
                continue;

            var member = hardwareModelEnumType.GetMember(enumName, BindingFlags.Public | BindingFlags.Static).FirstOrDefault();
            var originalName = member is null
                ? enumName
                : GetEnumOriginalName(member) ?? enumName;

            if (HardwareModelFriendlyNames.TryGetValue(originalName, out var mapped))
            {
                result[numericValue] = mapped;
                continue;
            }

            result[numericValue] = HumanizeToken(originalName);
        }

        return result;
    }

    private static bool TryGetEnumNumericValue(object enumObj, out int numericValue)
    {
        numericValue = 0;
        if (enumObj is null || !enumObj.GetType().IsEnum)
            return false;

        try
        {
            numericValue = Convert.ToInt32(enumObj, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetEnumOriginalName(object enumObj, out string originalName)
    {
        originalName = "";
        if (enumObj is null)
            return false;

        var enumType = enumObj.GetType();
        if (!enumType.IsEnum)
            return false;

        var enumName = enumObj.ToString();
        if (string.IsNullOrWhiteSpace(enumName))
            return false;

        var member = enumType.GetMember(enumName, BindingFlags.Public | BindingFlags.Static).FirstOrDefault();
        var candidate = member is null ? null : GetEnumOriginalName(member);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            originalName = candidate;
            return true;
        }

        originalName = enumName;
        return true;
    }

    private static string? GetEnumOriginalName(MemberInfo enumMember)
    {
        foreach (var attr in enumMember.GetCustomAttributes(inherit: false))
        {
            if (!string.Equals(attr.GetType().Name, "OriginalNameAttribute", StringComparison.Ordinal))
                continue;

            var nameProp = attr.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp?.GetValue(attr) is string name && !string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static string FormatProtoSingleLine(object obj)
    {
        var text = obj.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var parts = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => p.Trim())).Trim();
    }

    private static string BuildReadableVariantSummary(string variantCaseName, object? variantObj)
    {
        var key = NormalizeToken(variantCaseName);
        var (prefix, label) = key switch
        {
            "myinfo" => ("MYINFO", "MyInfo"),
            "config" => ("CFG", "Config"),
            "logrecord" => ("LOG", "LogRecord"),
            "configcompleteid" => ("CFGDONE", "ConfigComplete"),
            "rebooted" => ("REBOOT", "Rebooted"),
            "moduleconfig" => ("MODCFG", "ModuleConfig"),
            "channel" => ("CHAN", "Channel"),
            "queuestatus" => ("QUEUE", "QueueStatus"),
            "xmodempacket" => ("XMODEM", "XModem"),
            "metadata" => ("META", "DeviceMetadata"),
            "mqttclientproxymessage" => ("MQTT", "MqttClientProxy"),
            "fileinfo" => ("FILE", "FileInfo"),
            "clientnotification" => ("NOTIFY", "ClientNotification"),
            "deviceuiconfig" => ("UI", "DeviceUiConfig"),
            _ => ("OTHER", variantCaseName)
        };

        if (variantObj is null)
            return $"{prefix}: {label}";

        if (variantObj is bool b)
            return $"{prefix}: {label}: {b}";

        if (variantObj is int i)
            return $"{prefix}: {label}: {i}";

        if (variantObj is uint u)
            return $"{prefix}: {label}: {u}";

        if (variantObj is long l)
            return $"{prefix}: {label}: {l}";

        if (variantObj is ulong ul)
            return $"{prefix}: {label}: {ul}";

        if (key == "logrecord")
        {
            var level = "";
            var message = "";
            if (TryGetObj(variantObj, "Level", out var levelObj) && levelObj is not null)
                level = levelObj.ToString() ?? "";
            if (TryGetObj(variantObj, "Message", out var messageObj) && messageObj is not null)
                message = messageObj.ToString() ?? "";

            var text = string.IsNullOrWhiteSpace(level) ? message : $"{level}: {message}";
            return string.IsNullOrWhiteSpace(text) ? $"{prefix}: LogRecord" : $"{prefix}: LogRecord: {text}";
        }

        var formatted = FormatProtoSingleLine(variantObj);
        if (string.IsNullOrWhiteSpace(formatted))
            return $"{prefix}: {label}";

        return $"{prefix}: {label}: {formatted}";
    }

    private static string NormalizeToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var chars = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }
}
