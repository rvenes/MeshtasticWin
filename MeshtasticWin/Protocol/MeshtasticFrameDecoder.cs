using System;
using System.Collections.Generic;

namespace MeshtasticWin.Protocol;

/// <summary>
/// Meshtastic serial/TCP framing:
/// [0x94][0xC3][LEN_MSB][LEN_LSB] + protobuf payload (LEN bytes)
/// Resynchronizes by scanning for 0x94 0xC3.
/// </summary>
public sealed class MeshtasticFrameDecoder
{
    private const byte Sync1 = 0x94;
    private const byte Sync2 = 0xC3;

    // Guard against invalid lengths while resyncing in an arbitrary byte stream.
    private const int MaxFrameLen = 4096;

    private readonly List<byte> _buffer = new();

    public IEnumerable<byte[]> Feed(byte[] data)
    {
        if (data is null || data.Length == 0)
            yield break;

        _buffer.AddRange(data);

        while (true)
        {
            // Need at least 4 bytes for the header.
            if (_buffer.Count < 4)
                yield break;

            // 1) Find sync 0x94 0xC3 (debug text may be present in the same stream).
            int syncIndex = FindSync(_buffer);
            if (syncIndex < 0)
            {
                // No sync found: keep the last byte (it may be the start of sync).
                if (_buffer.Count > 1)
                    _buffer.RemoveRange(0, _buffer.Count - 1);
                yield break;
            }

            // Drop everything before sync.
            if (syncIndex > 0)
                _buffer.RemoveRange(0, syncIndex);

            if (_buffer.Count < 4)
                yield break;

            // 2) Read length (big-endian).
            int len = (_buffer[2] << 8) | _buffer[3];

            // Invalid length => continue resync.
            if (len <= 0 || len > MaxFrameLen)
            {
                // Drop the first sync byte and search again.
                _buffer.RemoveAt(0);
                continue;
            }

            // 3) Wait until full payload is in the buffer.
            int total = 4 + len;
            if (_buffer.Count < total)
                yield break;

            // 4) Extract payload.
            var payload = _buffer.GetRange(4, len).ToArray();

            // 5) Remove consumed data and yield payload.
            _buffer.RemoveRange(0, total);
            yield return payload;
        }
    }

    private static int FindSync(List<byte> buf)
    {
        for (int i = 0; i < buf.Count - 1; i++)
        {
            if (buf[i] == Sync1 && buf[i + 1] == Sync2)
                return i;
        }
        return -1;
    }
}
