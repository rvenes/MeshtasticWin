using Google.Protobuf;
using System;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MeshtasticWin.Protocol;

public static class ToRadioFactory
{
    private static Type? _toRadioType;
    private static Type? _meshPacketType;
    private static Type? _decodedType;

    private static PropertyInfo? _toRadioPacketProp;
    private static PropertyInfo? _toRadioWantConfigIdProp;

    private static PropertyInfo? _packetToProp;
    private static PropertyInfo? _packetDecodedProp;
    private static PropertyInfo? _packetIdProp;
    private static PropertyInfo? _packetWantAckProp;
    private static PropertyInfo? _packetHopLimitProp;
    private static PropertyInfo? _packetChannelProp;

    private static PropertyInfo? _decodedPortnumProp;
    private static PropertyInfo? _decodedPayloadProp;
    private static PropertyInfo? _decodedWantResponseProp;
    private static PropertyInfo? _decodedDestProp;

    public static IMessage CreateHelloRequest(uint wantConfigId = 1u)
    {
        EnsureCached();

        if (_toRadioType is null)
            throw new InvalidOperationException("ToRadio type not found");

        var msg = (IMessage)Activator.CreateInstance(_toRadioType)!;

        if (_toRadioWantConfigIdProp is null)
            throw new InvalidOperationException("ToRadio.WantConfigId property not found");

        _toRadioWantConfigIdProp.SetValue(msg, wantConfigId);
        return msg;
    }

    public static IMessage CreateTextMessage(
        string text,
        uint to,
        bool? wantAck,
        uint channel,
        out uint packetId)
    {
        EnsureCached();

        packetId = 0;

        if (_toRadioType is null || _meshPacketType is null || _decodedType is null)
            throw new InvalidOperationException("Proto types not found (ToRadio/MeshPacket/Decoded)");

        if (_toRadioPacketProp is null)
            throw new InvalidOperationException("ToRadio.Packet property not found");

        if (_packetToProp is null || _packetDecodedProp is null)
            throw new InvalidOperationException("MeshPacket.{To/Decoded} properties not found");

        if (_decodedPortnumProp is null || _decodedPayloadProp is null)
            throw new InvalidOperationException("Decoded.{Portnum/Payload} properties not found");

        bool wantAckResolved = wantAck ?? (to != 0xFFFFFFFF);

        var toRadio = (IMessage)Activator.CreateInstance(_toRadioType)!;
        var packet = Activator.CreateInstance(_meshPacketType)!;

        _packetToProp.SetValue(packet, to);

        // Channel: DM skal alltid vere 0
        if (_packetChannelProp is not null)
            _packetChannelProp.SetValue(packet, channel);

        // Hop limit: ensure non-zero TTL for routed messages
        if (_packetHopLimitProp is not null)
            _packetHopLimitProp.SetValue(packet, 3u);

        // Id: unik/non-zero
        if (_packetIdProp is not null)
        {
            packetId = PacketIdGenerator.Next();
            _packetIdProp.SetValue(packet, packetId);
        }

        // WantAck
        if (_packetWantAckProp is not null)
            _packetWantAckProp.SetValue(packet, wantAckResolved);

        // Decoded
        var decoded = Activator.CreateInstance(_decodedType)!;

        // Portnum = TEXT_MESSAGE_APP = 1
        var portPropType = _decodedPortnumProp.PropertyType;
        object portValue = portPropType.IsEnum ? Enum.ToObject(portPropType, 1) : 1;
        _decodedPortnumProp.SetValue(decoded, portValue);

        // Payload
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        _decodedPayloadProp.SetValue(decoded, ByteString.CopyFrom(bytes));

        _packetDecodedProp.SetValue(packet, decoded);
        _toRadioPacketProp.SetValue(toRadio, packet);

        return toRadio;
    }

    public static IMessage CreateNodeInfoRequest(uint to, out uint packetId)
        => CreateRequestMessage(to, portNum: 4, wantResponse: true, payload: Array.Empty<byte>(), dest: null, out packetId);

    public static IMessage CreatePositionRequest(uint to, out uint packetId)
        => CreateRequestMessage(to, portNum: 3, wantResponse: true, payload: Array.Empty<byte>(), dest: null, out packetId);

    public static IMessage CreateTraceRouteRequest(uint to, out uint packetId)
    {
        var payload = CreateRouteDiscoveryPayload();
        return CreateRequestMessage(to, portNum: 70, wantResponse: true, payload: payload, dest: to, out packetId);
    }

    private static IMessage CreateRequestMessage(
        uint to,
        int portNum,
        bool wantResponse,
        byte[] payload,
        uint? dest,
        out uint packetId)
    {
        EnsureCached();

        packetId = 0;

        if (_toRadioType is null || _meshPacketType is null || _decodedType is null)
            throw new InvalidOperationException("Proto types not found (ToRadio/MeshPacket/Decoded)");

        if (_toRadioPacketProp is null)
            throw new InvalidOperationException("ToRadio.Packet property not found");

        if (_packetToProp is null || _packetDecodedProp is null)
            throw new InvalidOperationException("MeshPacket.{To/Decoded} properties not found");

        if (_decodedPortnumProp is null || _decodedPayloadProp is null)
            throw new InvalidOperationException("Decoded.{Portnum/Payload} properties not found");

        var toRadio = (IMessage)Activator.CreateInstance(_toRadioType)!;
        var packet = Activator.CreateInstance(_meshPacketType)!;

        _packetToProp.SetValue(packet, to);

        if (_packetChannelProp is not null)
            _packetChannelProp.SetValue(packet, 0u);

        if (_packetHopLimitProp is not null)
            _packetHopLimitProp.SetValue(packet, 3u);

        if (_packetIdProp is not null)
        {
            packetId = PacketIdGenerator.Next();
            _packetIdProp.SetValue(packet, packetId);
        }

        if (_packetWantAckProp is not null)
            _packetWantAckProp.SetValue(packet, true);

        var decoded = Activator.CreateInstance(_decodedType)!;

        var portPropType = _decodedPortnumProp.PropertyType;
        object portValue = portPropType.IsEnum ? Enum.ToObject(portPropType, portNum) : portNum;
        _decodedPortnumProp.SetValue(decoded, portValue);

        _decodedPayloadProp.SetValue(decoded, ByteString.CopyFrom(payload ?? Array.Empty<byte>()));

        if (wantResponse && _decodedWantResponseProp is not null)
            _decodedWantResponseProp.SetValue(decoded, true);

        if (dest.HasValue && _decodedDestProp is not null)
            _decodedDestProp.SetValue(decoded, dest.Value);

        _packetDecodedProp.SetValue(packet, decoded);
        _toRadioPacketProp.SetValue(toRadio, packet);

        return toRadio;
    }

    private static void EnsureCached()
    {
        if (_toRadioType is not null)
            return;

        _toRadioType = FindTypeByName("ToRadio");
        _meshPacketType = FindTypeByName("MeshPacket");

        if (_toRadioType is null || _meshPacketType is null)
            return;

        _toRadioWantConfigIdProp =
            _toRadioType.GetProperty("WantConfigId", BindingFlags.Public | BindingFlags.Instance);

        _toRadioPacketProp =
            _toRadioType.GetProperty("Packet", BindingFlags.Public | BindingFlags.Instance);

        _packetToProp =
            _meshPacketType.GetProperty("To", BindingFlags.Public | BindingFlags.Instance);

        _packetDecodedProp =
            _meshPacketType.GetProperty("Decoded", BindingFlags.Public | BindingFlags.Instance);

        _packetIdProp =
            _meshPacketType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

        _packetWantAckProp =
            _meshPacketType.GetProperty("WantAck", BindingFlags.Public | BindingFlags.Instance);

        _packetChannelProp =
            _meshPacketType.GetProperty("Channel", BindingFlags.Public | BindingFlags.Instance);

        _packetHopLimitProp =
            _meshPacketType.GetProperty("HopLimit", BindingFlags.Public | BindingFlags.Instance);

        _decodedType = _packetDecodedProp?.PropertyType;

        if (_decodedType is not null)
        {
            _decodedPortnumProp =
                _decodedType.GetProperty("Portnum", BindingFlags.Public | BindingFlags.Instance);

            _decodedPayloadProp =
                _decodedType.GetProperty("Payload", BindingFlags.Public | BindingFlags.Instance);

            _decodedWantResponseProp =
                _decodedType.GetProperty("WantResponse", BindingFlags.Public | BindingFlags.Instance);

            _decodedDestProp =
                _decodedType.GetProperty("Dest", BindingFlags.Public | BindingFlags.Instance);
        }
    }

    private static byte[] CreateRouteDiscoveryPayload()
    {
        var routeType = FindTypeByName("RouteDiscovery");
        if (routeType is null)
            return Array.Empty<byte>();

        if (Activator.CreateInstance(routeType) is not IMessage msg)
            return Array.Empty<byte>();

        return msg.ToByteArray();
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
