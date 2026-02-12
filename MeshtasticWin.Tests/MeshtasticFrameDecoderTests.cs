using MeshtasticWin.Protocol;
using Xunit;

namespace MeshtasticWin.Tests;

public sealed class MeshtasticFrameDecoderTests
{
    [Fact]
    public void Feed_EmitsSingleFrame_WhenCompleteFrameArrives()
    {
        var decoder = new MeshtasticFrameDecoder();
        var payload = new byte[] { 0x10, 0x20, 0x30 };

        var frames = decoder.Feed(Wrap(payload)).ToList();

        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public void Feed_EmitsFrame_WhenHeaderAndPayloadArriveAcrossChunks()
    {
        var decoder = new MeshtasticFrameDecoder();
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var frame = Wrap(payload);

        var part1 = frame[..3];
        var part2 = frame[3..];

        Assert.Empty(decoder.Feed(part1));

        var frames = decoder.Feed(part2).ToList();

        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public void Feed_EmitsAllFrames_WhenMultipleFramesArriveTogether()
    {
        var decoder = new MeshtasticFrameDecoder();
        var payloadA = new byte[] { 0x01, 0x02 };
        var payloadB = new byte[] { 0xA1, 0xA2, 0xA3 };

        var bytes = Wrap(payloadA).Concat(Wrap(payloadB)).ToArray();
        var frames = decoder.Feed(bytes).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal(payloadA, frames[0]);
        Assert.Equal(payloadB, frames[1]);
    }

    [Fact]
    public void Feed_Resynchronizes_WhenGarbagePrecedesValidFrame()
    {
        var decoder = new MeshtasticFrameDecoder();
        var payload = new byte[] { 0x99, 0x88, 0x77 };

        var bytes = new byte[] { 0x00, 0x11, 0x22, 0x33 }
            .Concat(Wrap(payload))
            .ToArray();

        var frames = decoder.Feed(bytes).ToList();

        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public void Feed_Resynchronizes_AfterInvalidLengthHeader()
    {
        var decoder = new MeshtasticFrameDecoder();
        var payload = new byte[] { 0x55 };

        var invalidHeader = new byte[] { 0x94, 0xC3, 0x20, 0x00, 0xAB, 0xCD };
        var bytes = invalidHeader.Concat(Wrap(payload)).ToArray();

        var frames = decoder.Feed(bytes).ToList();

        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public void Feed_HandlesSplitSyncMarker_AcrossChunks()
    {
        var decoder = new MeshtasticFrameDecoder();

        Assert.Empty(decoder.Feed(new byte[] { 0x01, 0x94 }));

        var frames = decoder.Feed(new byte[] { 0xC3, 0x00, 0x01, 0x7A }).ToList();

        Assert.Single(frames);
        Assert.Equal(new byte[] { 0x7A }, frames[0]);
    }

    [Fact]
    public void Feed_IgnoresZeroLengthHeader_AndFindsNextFrame()
    {
        var decoder = new MeshtasticFrameDecoder();
        var payload = new byte[] { 0x42, 0x43 };

        var zeroLengthHeader = new byte[] { 0x94, 0xC3, 0x00, 0x00 };
        var bytes = zeroLengthHeader.Concat(Wrap(payload)).ToArray();

        var frames = decoder.Feed(bytes).ToList();

        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    private static byte[] Wrap(byte[] payload)
    {
        var len = payload.Length;
        return new byte[]
        {
            0x94,
            0xC3,
            (byte)((len >> 8) & 0xFF),
            (byte)(len & 0xFF)
        }.Concat(payload).ToArray();
    }
}
