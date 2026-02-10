using System;

namespace MeshtasticWin.Models;

public sealed class MessageLive
{
    public string FromIdHex { get; init; } = "";
    public string FromName { get; init; } = "";

    public string ToIdHex { get; init; } = "";   // "0x........" or "0xffffffff"
    public string ToName { get; init; } = "";

    public bool IsMine { get; init; }
    public bool IsDirect => !string.IsNullOrWhiteSpace(ToIdHex) &&
                            !string.Equals(ToIdHex, "0xffffffff", StringComparison.OrdinalIgnoreCase);

    public string Text { get; init; } = "";
    public string When { get; init; } = "";
    public DateTime WhenUtc { get; init; }

    public uint PacketId { get; init; }

    // Status ticks: single tick vs double tick
    public bool IsHeard { get; init; }        // At least one ACK from any node
    public bool IsDelivered { get; init; }    // ACK from DM recipient (DM only)

    // Tracks the DM recipient (nodeNum)
    public uint DmTargetNodeNum { get; init; } // 0 when not DM

    public string Header
    {
        get
        {
            if (IsMine) return "Me";

            var id = FromIdHex ?? "";
            var name = FromName ?? "";

            if (string.IsNullOrWhiteSpace(name)) return id;
            if (!string.IsNullOrWhiteSpace(id) &&
                !string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                return $"{name} ({id})";

            return name;
        }
    }

    public static MessageLive CreateIncoming(string fromIdHex, string fromName, string toIdHex, string toName, string text)
        => new()
        {
            FromIdHex = fromIdHex ?? "",
            FromName = fromName ?? "",
            ToIdHex = toIdHex ?? "",
            ToName = toName ?? "",
            Text = text ?? "",
            When = DateTime.Now.ToString("HH:mm:ss"),
            WhenUtc = DateTime.UtcNow,
            IsMine = false,
            PacketId = 0,
            IsHeard = false,
            IsDelivered = false,
            DmTargetNodeNum = 0
        };

    public static MessageLive CreateOutgoing(string toIdHex, string toName, string text, uint packetId, uint dmTargetNodeNum)
        => new()
        {
            FromIdHex = "",
            FromName = "",
            ToIdHex = toIdHex ?? "",
            ToName = toName ?? "",
            Text = text ?? "",
            When = DateTime.Now.ToString("HH:mm:ss"),
            WhenUtc = DateTime.UtcNow,
            IsMine = true,
            PacketId = packetId,
            IsHeard = false,
            IsDelivered = false,
            DmTargetNodeNum = dmTargetNodeNum
        };

    public MessageLive WithAckFrom(uint ackFromNodeNum)
    {
        // Broadcast: heard only
        if (!IsDirect || DmTargetNodeNum == 0)
        {
            if (IsHeard) return this;
            return Clone(heard: true, delivered: false);
        }

        // DM:
        // - heard: ACK from any node
        // - delivered: ACK from DM recipient
        bool deliveredNow = (ackFromNodeNum == DmTargetNodeNum);
        bool heardNow = true;

        if (IsHeard && (IsDelivered || !deliveredNow))
            return this;

        return Clone(heard: heardNow || IsHeard, delivered: IsDelivered || deliveredNow);
    }

    private MessageLive Clone(bool heard, bool delivered)
        => new()
        {
            FromIdHex = FromIdHex,
            FromName = FromName,
            ToIdHex = ToIdHex,
            ToName = ToName,
            IsMine = IsMine,
            Text = Text,
            When = When,
            WhenUtc = WhenUtc,
            PacketId = PacketId,
            IsHeard = heard,
            IsDelivered = delivered,
            DmTargetNodeNum = DmTargetNodeNum
        };
}
