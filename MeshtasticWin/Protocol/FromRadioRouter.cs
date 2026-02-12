using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;
using MeshtasticWin.Services;
using MeshtasticWin.Models;

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

    public static bool TryHandle(byte[] payload, Action<Action> runOnUi, Action<string> logToUi, out string summary)
        => TryApplyFromRadio(payload, runOnUi, logToUi, out summary);

    public static bool TryApplyFromRadio(byte[] frame, Action<Action> runOnUi, Action<string> logToUi, out string summary)
    {
        summary = "unknown";

        try
        {
            var fromRadioType = FindTypeByName("FromRadio");
            if (fromRadioType is null)
            {
                summary = "FromRadio type not found";
                return false;
            }

            var parserProp = fromRadioType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);
            var parser = parserProp?.GetValue(null);
            if (parser is null)
            {
                summary = "FromRadio.Parser missing";
                return false;
            }

            var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
            var fromRadioObj = parseFrom?.Invoke(parser, new object[] { frame });
            if (fromRadioObj is null)
            {
                summary = "ParseFrom returned null";
                return false;
            }

            var variantCaseName = TryGetFromRadioVariantCaseName(fromRadioObj);
            if (!string.IsNullOrWhiteSpace(variantCaseName) && !IsNoneVariantCase(variantCaseName))
            {
                var normalizedCase = NormalizeToken(variantCaseName);
                TryGetFromRadioVariantObject(fromRadioObj, variantCaseName, out var variantObj);

                if (normalizedCase == "packet" && variantObj is not null)
                {
                    summary = "Packet";
                    runOnUi(() => ApplyPacketObject(variantObj, logToUi));
                    return true;
                }

                if (normalizedCase == "nodeinfo" && variantObj is not null)
                {
                    summary = "NodeInfo";
                    runOnUi(() => ApplyNodeInfoObject(variantObj, logToUi));
                    return true;
                }

                if (normalizedCase == "myinfo" && variantObj is not null)
                {
                    summary = "MyInfo";
                    runOnUi(() => ApplyMyInfoObject(variantObj));
                    return true;
                }

                if (normalizedCase == "metadata" && variantObj is not null)
                {
                    summary = "DeviceMetadata";
                    runOnUi(() => ApplyDeviceMetadataObject(variantObj));
                    return true;
                }

                summary = BuildReadableVariantSummary(variantCaseName, variantObj);
                return true;
            }

            var packetProp = fromRadioType.GetProperty("Packet", BindingFlags.Public | BindingFlags.Instance);
            var packetObj = packetProp?.GetValue(fromRadioObj);
            if (packetObj is not null)
            {
                summary = "Packet";
                runOnUi(() => ApplyPacketObject(packetObj, logToUi));
                return true;
            }

            var nodeInfoProp =
                fromRadioType.GetProperty("NodeInfo", BindingFlags.Public | BindingFlags.Instance) ??
                fromRadioType.GetProperty("Nodeinfo", BindingFlags.Public | BindingFlags.Instance);

            var nodeInfoObj = nodeInfoProp?.GetValue(fromRadioObj);
            if (nodeInfoObj is not null)
            {
                summary = "NodeInfo";
                runOnUi(() => ApplyNodeInfoObject(nodeInfoObj, logToUi));
                return true;
            }

            summary = "other";
            return true;
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

        var compressedType = FindTypeByName("Compressed");
        if (compressedType is null)
            return false;

        var parser = compressedType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (parser is null)
            return false;

        var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
        if (parseFrom is null)
            return false;

        object? compressedObj;
        try
        {
            compressedObj = parseFrom.Invoke(parser, new object[] { payloadBytes });
        }
        catch
        {
            return false;
        }

        if (compressedObj is null)
            return false;

        if (TryGetObj(compressedObj, "Portnum", out var wrappedPortObj) &&
            wrappedPortObj is not null)
        {
            var wrappedPortNum = ConvertToInt(wrappedPortObj);
            if (wrappedPortNum != -1 && wrappedPortNum != TextMessagePortNum)
                return false;
        }

        if (!TryGetBytes(compressedObj, "Data", out var wrappedData) || wrappedData.Length == 0)
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
        summary = "Position type not found";
        var positionType = FindTypeByName("Position");
        if (positionType is null)
            return false;

        var parser = positionType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (parser is null)
        {
            summary = "Position.Parser missing";
            return false;
        }

        var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
        if (parseFrom is null)
        {
            summary = "Position.Parser.ParseFrom(byte[]) missing";
            return false;
        }

        object? posObj;
        try
        {
            posObj = parseFrom.Invoke(parser, new object[] { payloadBytes });
        }
        catch (Exception ex)
        {
            summary = $"Position.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (posObj is null)
        {
            summary = "Position.ParseFrom returned null";
            return false;
        }

        if (!TryGetInt(posObj, "LatitudeI", out var latI) || !TryGetInt(posObj, "LongitudeI", out var lonI))
        {
            summary = "missing LatitudeI/LongitudeI";
            return false;
        }

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

        var telemetryType = FindTypeByName("Telemetry");
        if (telemetryType is null)
        {
            summary = "Telemetry type not found";
            return false;
        }

        var parser = telemetryType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (parser is null)
        {
            summary = "Telemetry.Parser missing";
            return false;
        }

        var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
        if (parseFrom is null)
        {
            summary = "Telemetry.Parser.ParseFrom(byte[]) missing";
            return false;
        }

        object? telemetryObj;
        try
        {
            telemetryObj = parseFrom.Invoke(parser, new object[] { payloadBytes });
        }
        catch (Exception ex)
        {
            summary = $"Telemetry.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (telemetryObj is null)
        {
            summary = "Telemetry.ParseFrom returned null";
            return false;
        }

        var receiveTimeUtc = DateTime.UtcNow;
        var tsUtc = receiveTimeUtc;
        if (TryGetUInt(telemetryObj, "Time", out var timeSec))
            tsUtc = NormalizeTelemetryTimestamp(timeSec, receiveTimeUtc);

        var fromIdHex = $"0x{fromNodeNum:x8}";
        var logType = (NodeLogType?)null;
        var label = "";
        object? metricsObj = null;

        if (TryGetObj(telemetryObj, "DeviceMetrics", out metricsObj) && metricsObj is not null)
        {
            logType = NodeLogType.DeviceMetrics;
            label = "device";
        }
        else if (TryGetObj(telemetryObj, "EnvironmentMetrics", out metricsObj) && metricsObj is not null)
        {
            logType = NodeLogType.EnvironmentMetrics;
            label = "environment";
        }
        else if (TryGetObj(telemetryObj, "PowerMetrics", out metricsObj) && metricsObj is not null)
        {
            logType = NodeLogType.PowerMetrics;
            label = "power";
        }
        else if (TryGetObj(telemetryObj, "AirQualityMetrics", out metricsObj) && metricsObj is not null)
        {
            logType = NodeLogType.EnvironmentMetrics;
            label = "air_quality";
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

        var routeType = FindTypeByName("RouteDiscovery");
        if (routeType is null)
        {
            summary = "RouteDiscovery type not found";
            return false;
        }

        var parser = routeType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (parser is null)
        {
            summary = "RouteDiscovery.Parser missing";
            return false;
        }

        var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
        if (parseFrom is null)
        {
            summary = "RouteDiscovery.Parser.ParseFrom(byte[]) missing";
            return false;
        }

        object? routeObj;
        try
        {
            routeObj = parseFrom.Invoke(parser, new object[] { payloadBytes });
        }
        catch (Exception ex)
        {
            summary = $"RouteDiscovery.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (routeObj is null)
        {
            summary = "RouteDiscovery.ParseFrom returned null";
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

        var routingType = FindTypeByName("Routing");
        if (routingType is null)
        {
            summary = "Routing type not found";
            return false;
        }

        var parser = routingType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (parser is null)
        {
            summary = "Routing.Parser missing";
            return false;
        }

        var parseFrom = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
        if (parseFrom is null)
        {
            summary = "Routing.Parser.ParseFrom(byte[]) missing";
            return false;
        }

        object? routingObj;
        try
        {
            routingObj = parseFrom.Invoke(parser, new object[] { payloadBytes });
        }
        catch (Exception ex)
        {
            summary = $"Routing.ParseFrom exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (routingObj is null)
        {
            summary = "Routing.ParseFrom returned null";
            return false;
        }

        object? routeObj = null;
        var variant = "";
        var variantCaseProp = routingType.GetProperty("VariantCase", BindingFlags.Public | BindingFlags.Instance);
        if (variantCaseProp?.GetValue(routingObj) is Enum variantCase)
        {
            var variantText = variantCase.ToString();
            if (string.Equals(variantText, "RouteRequest", StringComparison.OrdinalIgnoreCase))
            {
                routeObj = routingType.GetProperty("RouteRequest", BindingFlags.Public | BindingFlags.Instance)?.GetValue(routingObj);
                variant = "route_request";
            }
            else if (string.Equals(variantText, "RouteReply", StringComparison.OrdinalIgnoreCase))
            {
                routeObj = routingType.GetProperty("RouteReply", BindingFlags.Public | BindingFlags.Instance)?.GetValue(routingObj);
                variant = "route_reply";
            }
        }

        if (routeObj is null && TryGetObj(routingObj, "RouteRequest", out var routeRequestObj))
        {
            routeObj = routeRequestObj;
            variant = "route_request";
        }
        else if (routeObj is null && TryGetObj(routingObj, "RouteReply", out var routeReplyObj))
        {
            routeObj = routeReplyObj;
            variant = "route_reply";
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

        return numericValue.ToString(CultureInfo.InvariantCulture);
    }

    private static string HumanizeToken(string raw)
    {
        var parts = raw.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return raw;

        return string.Join(" ", parts.Select(static part =>
        {
            if (part.Length <= 3 || part.All(char.IsDigit))
                return part.ToUpperInvariant();

            return char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant();
        }));
    }

    private static string FormatProtoSingleLine(object obj)
    {
        var text = obj.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var parts = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => p.Trim())).Trim();
    }

    private static Type? FindTypeByName(string typeName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .SelectMany(t => t)
            .FirstOrDefault(t => t.IsClass && t.Name == typeName);
    }

    private static string? TryGetFromRadioVariantCaseName(object fromRadioObj)
    {
        var t = fromRadioObj.GetType();
        var prop =
            t.GetProperty("PayloadVariantCase", BindingFlags.Public | BindingFlags.Instance) ??
            t.GetProperty("PayloadCase", BindingFlags.Public | BindingFlags.Instance) ??
            t.GetProperty("VariantCase", BindingFlags.Public | BindingFlags.Instance);

        return prop?.GetValue(fromRadioObj)?.ToString();
    }

    private static bool TryGetFromRadioVariantObject(object fromRadioObj, string variantCaseName, out object? value)
    {
        value = null;
        var caseKey = NormalizeToken(variantCaseName);
        if (string.IsNullOrWhiteSpace(caseKey))
            return false;

        var props = fromRadioObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var p in props)
        {
            if (p.GetIndexParameters().Length != 0)
                continue;

            var key = NormalizeToken(p.Name);
            if (key != caseKey)
                continue;

            value = p.GetValue(fromRadioObj);
            return true;
        }

        return false;
    }

    private static bool IsNoneVariantCase(string variantCaseName)
    {
        var key = NormalizeToken(variantCaseName);
        return key is "none" or "payloadvariantnotset" or "notset" or "";
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
