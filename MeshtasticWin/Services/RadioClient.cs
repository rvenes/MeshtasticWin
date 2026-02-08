using Meshtastic.Transport.Serial;
using MeshtasticWin.Parsing;
using MeshtasticWin.Protocol;
using System;
using System.Collections.ObjectModel;

namespace MeshtasticWin.Services;

public sealed class RadioClient
{
    public static RadioClient Instance { get; } = new();

    private const int MaxLogLines = 500;

    private SerialTransport? _transport;
    private readonly SerialTextDecoder _decoder = new();
    private readonly MeshtasticFrameDecoder _frameDecoder = new();

    public bool IsConnected { get; private set; }
    public string? PortName { get; private set; }

    public ObservableCollection<string> LogLines { get; } = new();

    public event Action? ConnectionChanged;

    private RadioClient() { }

    public void AddLogFromUiThread(string line)
    {
        LogLines.Insert(0, line);
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(LogLines.Count - 1);
    }

    public async System.Threading.Tasks.Task ConnectAsync(
        string port,
        Action<Action> runOnUi,
        Action<string> logToUi)
    {
        if (IsConnected)
            return;

        PortName = port;

        _transport = new SerialTransport(port);

        _transport.Log += msg => logToUi(msg);

        _transport.BytesReceived += bytes =>
        {
            foreach (var line in _decoder.Feed(bytes))
            {
                if (!LooksLikeDebugText(line))
                    continue;

                logToUi($"TXT: {line}");

                runOnUi(() =>
                {
                    try { MeshDebugLineParser.Consume(line); }
                    catch (Exception ex) { logToUi($"MeshDebugLineParser error: {ex.Message}"); }
                });
            }

            foreach (var frame in _frameDecoder.Feed(bytes))
            {
                if (FromRadioRouter.TryHandle(frame, runOnUi, logToUi, out var summary))
                    logToUi($"PROTOBUF FromRadio: {summary} ({frame.Length} bytes)");
                else
                    logToUi($"PROTOBUF frame (unknown): {summary} ({frame.Length} bytes)");
            }
        };

        await _transport.ConnectAsync();

        IsConnected = true;
        ConnectionChanged?.Invoke();
        MeshtasticWin.AppState.SetConnectedNodeIdHex(null);

        try
        {
            var helloMsg = ToRadioFactory.CreateHelloRequest(1u);
            var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)helloMsg);
            await _transport.SendAsync(framed);
            logToUi("Sent ToRadio: WantConfigId=1");
        }
        catch (Exception ex)
        {
            logToUi($"Failed to send ToRadio hello: {ex.Message}");
        }
    }

    public async System.Threading.Tasks.Task DisconnectAsync()
    {
        if (_transport is null)
            return;

        await _transport.DisconnectAsync();
        _transport = null;

        IsConnected = false;
        PortName = null;
        ConnectionChanged?.Invoke();
        MeshtasticWin.AppState.SetConnectedNodeIdHex(null);
    }

    // Broadcast
    public async System.Threading.Tasks.Task<uint> SendTextAsync(string text)
        => await SendTextAsync(text, (uint?)null);

    // DM når toNodeNum har verdi.
    public async System.Threading.Tasks.Task<uint> SendTextAsync(string text, uint? toNodeNum)
    {
        if (!IsConnected || _transport is null)
            throw new InvalidOperationException("Not connected");

        text ??= "";
        text = text.Trim();
        if (text.Length == 0)
            return 0;

        bool isDm = toNodeNum.HasValue;
        uint to = isDm ? toNodeNum!.Value : 0xFFFFFFFF;

        // Viktig: user vil ha ACK i primary òg -> set WantAck=true på broadcast òg.
        // Merk: ikkje alle noder svarar med ACK på broadcast, men når dei gjer, kan me vise ✔.
        bool wantAck = true;

        var msg = ToRadioFactory.CreateTextMessage(
            text: text,
            to: to,
            wantAck: wantAck,
            channel: 0,
            out uint packetId);

        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);

        await _transport.SendAsync(framed);
        return packetId;
    }

    public async System.Threading.Tasks.Task<uint> SendNodeInfoRequestAsync(uint toNodeNum)
    {
        if (!IsConnected || _transport is null)
            throw new InvalidOperationException("Not connected");

        var msg = ToRadioFactory.CreateNodeInfoRequest(toNodeNum, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        await _transport.SendAsync(framed);
        return packetId;
    }

    public async System.Threading.Tasks.Task<uint> SendPositionRequestAsync(uint toNodeNum)
    {
        if (!IsConnected || _transport is null)
            throw new InvalidOperationException("Not connected");

        var msg = ToRadioFactory.CreatePositionRequest(toNodeNum, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        await _transport.SendAsync(framed);
        return packetId;
    }

    public async System.Threading.Tasks.Task<uint> SendTraceRouteRequestAsync(uint toNodeNum)
    {
        if (!IsConnected || _transport is null)
            throw new InvalidOperationException("Not connected");

        var msg = ToRadioFactory.CreateTraceRouteRequest(toNodeNum, out var packetId);
        var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
        await _transport.SendAsync(framed);
        return packetId;
    }

    private static bool LooksLikeDebugText(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (line.Contains('\uFFFD'))
            return false;

        if (line.Length < 8)
            return false;

        var s = line.TrimStart();

        if (s.StartsWith("INFO", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("DEBUG", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase)) return true;

        if (line.Contains("[Router]", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("[SerialConsole]", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("[RadioIf]", StringComparison.OrdinalIgnoreCase)) return true;

        if (line.Contains(" | ") && line.Contains('[') && line.Contains(']'))
            return true;

        return false;
    }
}
