using Google.Protobuf;
using Meshtastic.Protobufs;
using System;
using System.Globalization;
using System.Text;

namespace MeshtasticWin.Protocol;

public static class ToRadioFactory
{
    public const int MaxTextPayloadBytes = 200;
    private const uint BroadcastNodeNum = 0xFFFFFFFF;
    private const uint DefaultHopLimit = 3;

    public static IMessage CreateHelloRequest(uint wantConfigId = 1u)
        => new ToRadio { WantConfigId = wantConfigId };

    public static IMessage CreateDisconnectNotice()
        => new ToRadio { Disconnect = true };

    public static IMessage CreateHeartbeat(uint nonce)
        => new ToRadio
        {
            Heartbeat = new Heartbeat
            {
                Nonce = nonce
            }
        };

    public static IMessage CreateTextMessage(
        string text,
        uint to,
        bool? wantAck,
        uint channel,
        out uint packetId)
    {
        var normalizedText = NormalizeTextPayload(text, MaxTextPayloadBytes);
        var bytes = Encoding.UTF8.GetBytes(normalizedText);
        var wantAckResolved = wantAck ?? (to != BroadcastNodeNum);
        packetId = PacketIdGenerator.Next();

        var packet = new MeshPacket
        {
            To = to,
            Channel = channel,
            HopLimit = DefaultHopLimit,
            Id = packetId,
            WantAck = wantAckResolved,
            Decoded = new Data
            {
                Portnum = PortNum.TextMessageApp,
                Payload = ByteString.CopyFrom(bytes)
            }
        };

        return new ToRadio { Packet = packet };
    }

    public static IMessage CreateNodeInfoRequest(uint to, out uint packetId)
        => CreateRequestMessage(
            to,
            portNum: PortNum.NodeinfoApp,
            wantResponse: true,
            payload: Array.Empty<byte>(),
            dest: null,
            out packetId);

    public static IMessage CreatePositionRequest(uint to, out uint packetId)
        => CreateRequestMessage(
            to,
            portNum: PortNum.PositionApp,
            wantResponse: true,
            payload: Array.Empty<byte>(),
            dest: null,
            out packetId);

    public static IMessage CreateTraceRouteRequest(uint to, out uint packetId)
    {
        var payload = CreateRouteDiscoveryPayload();
        return CreateRequestMessage(
            to,
            portNum: PortNum.TracerouteApp,
            wantResponse: true,
            payload: payload,
            dest: to,
            out packetId);
    }

    private static IMessage CreateRequestMessage(
        uint to,
        PortNum portNum,
        bool wantResponse,
        byte[] payload,
        uint? dest,
        out uint packetId)
    {
        packetId = PacketIdGenerator.Next();

        var decoded = new Data
        {
            Portnum = portNum,
            Payload = ByteString.CopyFrom(payload ?? Array.Empty<byte>()),
            WantResponse = wantResponse
        };

        if (dest.HasValue)
            decoded.Dest = dest.Value;

        var packet = new MeshPacket
        {
            To = to,
            Channel = 0,
            HopLimit = DefaultHopLimit,
            Id = packetId,
            WantAck = true,
            Decoded = decoded
        };

        return new ToRadio { Packet = packet };
    }

    private static byte[] CreateRouteDiscoveryPayload()
    {
        var route = new RouteDiscovery();
        return route.ToByteArray();
    }

    public static string NormalizeTextPayload(string? text, int maxPayloadBytes = MaxTextPayloadBytes)
    {
        var input = text ?? "";
        if (input.Length == 0 || maxPayloadBytes <= 0)
            return "";

        if (Encoding.UTF8.GetByteCount(input) <= maxPayloadBytes)
            return input;

        var sb = new StringBuilder(input.Length);
        var byteCount = 0;
        var elements = StringInfo.GetTextElementEnumerator(input);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement() ?? "";
            if (element.Length == 0)
                continue;

            var elementBytes = Encoding.UTF8.GetByteCount(element);
            if (byteCount + elementBytes > maxPayloadBytes)
                break;

            sb.Append(element);
            byteCount += elementBytes;
        }

        return sb.ToString();
    }
}
