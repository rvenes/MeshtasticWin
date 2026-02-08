using System;
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
    private const int PositionPortNum = 3;    // POSITION_APP
    private const int RoutingAppPortNum = 5;  // ROUTING_APP
    private const int DetectionSensorPortNum = 10; // DETECTION_SENSOR_APP
    private const int TelemetryAppPortNum = 67; // TELEMETRY_APP
    private const int TraceRoutePortNum = 70; // TRACEROUTE_APP
    private const int MaxMessages = 500;
    private static readonly DateTime MinTelemetryTimestampUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

        uint toNodeNum = 0xFFFFFFFF;
        TryGetUInt(packetObj, "To", out toNodeNum);

        var fromIdHex = $"0x{fromNodeNum:x8}";
        var toIdHex = $"0x{toNodeNum:x8}";

        // Sørg for node-objekt (så SNR/RSSI kan oppdaterast sjølv om NodeInfo ikkje er kome enno)
        var fromNode = EnsureNode(fromIdHex, fromNodeNum);
        fromNode.Touch();

        // SNR/RSSI (feltnamn varierer litt – prøv fleire)
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

        // --- TEXT_MESSAGE_APP ---
        if (portNum != TextMessagePortNum)
            return;

        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return;

        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(payloadBytes).Trim('\0', '\r', '\n');
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        var toNode = MeshtasticWin.AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, toIdHex, StringComparison.OrdinalIgnoreCase));
        var toName = (toNodeNum == 0xFFFFFFFF) ? "Primary" : (toNode?.Name ?? toIdHex);

        var msg = MessageLive.CreateIncoming(fromIdHex, fromNode.Name, toIdHex, toName, text);

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

    private static void ApplyNodeInfoObject(object nodeInfoObj, Action<string> logToUi)
    {
        if (!TryGetUInt(nodeInfoObj, "Num", out var nodeNum) && !TryGetUInt(nodeInfoObj, "NodeNum", out nodeNum))
            return;

        var idHex = $"0x{nodeNum:x8}";
        var node = EnsureNode(idHex, nodeNum);
        if (RadioClient.Instance.IsConnected && string.IsNullOrWhiteSpace(MeshtasticWin.AppState.ConnectedNodeIdHex))
            MeshtasticWin.AppState.SetConnectedNodeIdHex(idHex);

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
        }

        // GPS bootstrap frå NodeInfo om den finst
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

    private static NodeLive EnsureNode(string idHex, uint nodeNum)
    {
        var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n => string.Equals(n.IdHex, idHex, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            node = new NodeLive(idHex);
            MeshtasticWin.AppState.Nodes.Insert(0, node);
        }

        // om NodeLive har NodeNum-property (du la den inn)
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

        logToUi($"ACK (ukjent request_id=0x{requestId:x8})");
    }

    // GPS frå packet.Payload (protobuf Position)
    private static bool TryHandlePositionFromPayload(object decodedObj, uint fromNodeNum, string source, Action<string> logToUi, out string summary)
    {
        summary = "payload empty";
        var payloadBytes = TryGetPayloadBytes(decodedObj);
        if (payloadBytes is null || payloadBytes.Length == 0)
            return false;

        return TryParsePositionBytesAndApply(payloadBytes, fromNodeNum, source, logToUi, out summary);
    }

    // GPS frå NodeInfo.Position / LastPosition (protobuf Position-objekt)
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
            // dersom din GpsArchive har annan signatur: tilpass her
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

        var v = p.GetValue(decodedObj);
        if (v is null) return -1;

        if (v.GetType().IsEnum)
            return (int)v;

        if (v is int i) return i;
        if (v is uint u) return (int)u;

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
}
