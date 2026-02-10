using Meshtastic.Core;
using Meshtastic.Transport.Serial;
using MeshtasticWin.Parsing;
using MeshtasticWin.Protocol;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace MeshtasticWin.Services;

public sealed class RadioClient
{
    public static RadioClient Instance { get; } = new();

    private const int MaxLogLines = 500;
    private const string LiveDebugLogRootDirectory = @"H:\Koding\MeshtasticWin\Debuglogg";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private IRadioTransport? _transport;
    private readonly SerialTextDecoder _decoder = new();
    private readonly MeshtasticFrameDecoder _frameDecoder = new();
    private int _disconnecting;
    private readonly object _liveLogLock = new();
    private readonly Queue<string> _liveLogQueue = new();
    private readonly SemaphoreSlim _liveLogSignal = new(0, int.MaxValue);
    private CancellationTokenSource? _liveLogCts;
    private Task? _liveLogTask;
    private string? _liveLogPath;
    private readonly object _rxQueueLock = new();
    private readonly Queue<byte[]> _rxQueue = new();
    private readonly SemaphoreSlim _rxQueueSignal = new(0, int.MaxValue);
    private CancellationTokenSource? _rxPumpCts;
    private Task? _rxPumpTask;
    private Action<string>? _transportLogHandler;
    private Action<byte[]>? _transportBytesHandler;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private uint _heartbeatNonce;

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

        AppendToLiveDebugLog(line);
    }

    private void AppendToLiveDebugLog(string line)
    {
        try
        {
            EnsureLiveLogWriterStarted();
            lock (_liveLogLock)
                _liveLogQueue.Enqueue(line);
            _liveLogSignal.Release();
        }
        catch
        {
            // Never let file logging break runtime logging.
        }
    }

    private void EnsureLiveLogWriterStarted()
    {
        lock (_liveLogLock)
        {
            if (_liveLogTask is { IsCompleted: false })
                return;

            _liveLogPath = BuildScopedLiveLogPath();

            _liveLogCts = new CancellationTokenSource();
            var ct = _liveLogCts.Token;
            _liveLogTask = Task.Run(async () =>
            {
                var batch = new List<string>(128);

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await _liveLogSignal.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    batch.Clear();
                    lock (_liveLogLock)
                    {
                        while (_liveLogQueue.Count > 0 && batch.Count < 512)
                            batch.Add(_liveLogQueue.Dequeue());
                    }

                    if (batch.Count == 0 || string.IsNullOrWhiteSpace(_liveLogPath))
                        continue;

                    try
                    {
                        File.AppendAllLines(_liveLogPath, batch);
                    }
                    catch
                    {
                        // Ignore file write issues; runtime logging must continue.
                    }
                }
            }, ct);
        }
    }

    public void RotateLiveLogForCurrentScope()
    {
        lock (_liveLogLock)
            _liveLogPath = BuildScopedLiveLogPath();
    }

    private static string BuildScopedLiveLogPath()
    {
        var scopedDebugDir = Path.Combine(LiveDebugLogRootDirectory, AppDataPaths.ActiveNodeScope);
        Directory.CreateDirectory(scopedDebugDir);
        return Path.Combine(scopedDebugDir, $"connect_live_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    public async System.Threading.Tasks.Task ConnectAsync(
        string port,
        Action<Action> runOnUi,
        Action<string> logToUi)
    {
        await ConnectWithTransportAsync(
            new SerialTransport(port),
            port,
            runOnUi,
            logToUi);
    }

    public async System.Threading.Tasks.Task ConnectTcpAsync(
        string host,
        int port,
        Action<Action> runOnUi,
        Action<string> logToUi)
    {
        await ConnectWithTransportAsync(
            new TcpTransport(host, port),
            $"TCP {host}:{port}",
            runOnUi,
            logToUi);
    }

    public async System.Threading.Tasks.Task ConnectBluetoothAsync(
        string deviceId,
        string deviceName,
        Action<Action> runOnUi,
        Action<string> logToUi)
    {
        await ConnectWithTransportAsync(
            new BluetoothLeTransport(deviceId),
            $"Bluetooth {deviceName}",
            runOnUi,
            logToUi);
    }

    public async System.Threading.Tasks.Task DisconnectAsync()
    {
        var transport = _transport;
        if (transport is null)
            return;

        Interlocked.Exchange(ref _disconnecting, 1);
        IsConnected = false;
        ConnectionChanged?.Invoke();

        if (_transportLogHandler is not null)
            transport.Log -= _transportLogHandler;
        if (_transportBytesHandler is not null)
            transport.BytesReceived -= _transportBytesHandler;

        _transportLogHandler = null;
        _transportBytesHandler = null;

        StopHeartbeatPump();

        try
        {
            var disconnectMsg = ToRadioFactory.CreateDisconnectNotice();
            var disconnectFrame = MeshtasticWire.Wrap((Google.Protobuf.IMessage)disconnectMsg);
            await transport.SendAsync(disconnectFrame);
        }
        catch
        {
            // Optional notice; ignore on shutdown.
        }

        StopRxPump();
        await transport.DisconnectAsync();
        _transport = null;

        PortName = null;
        MeshtasticWin.AppState.SetConnectedNodeIdHex(null);
    }

    private async System.Threading.Tasks.Task ConnectWithTransportAsync(
        IRadioTransport transport,
        string endpointName,
        Action<Action> runOnUi,
        Action<string> logToUi)
    {
        if (IsConnected)
            return;

        Interlocked.Exchange(ref _disconnecting, 0);
        StopHeartbeatPump();

        PortName = endpointName;
        _transport = transport;

        StartRxPump(runOnUi, logToUi);

        _transportLogHandler = msg => logToUi(msg);
        _transportBytesHandler = bytes =>
        {
            if (Volatile.Read(ref _disconnecting) != 0 || !IsConnected)
                return;

            lock (_rxQueueLock)
                _rxQueue.Enqueue(bytes);

            _rxQueueSignal.Release();
        };

        transport.Log += _transportLogHandler;
        transport.BytesReceived += _transportBytesHandler;

        try
        {
            await transport.ConnectAsync();
        }
        catch
        {
            if (_transportLogHandler is not null)
                transport.Log -= _transportLogHandler;
            if (_transportBytesHandler is not null)
                transport.BytesReceived -= _transportBytesHandler;

            _transportLogHandler = null;
            _transportBytesHandler = null;
            StopHeartbeatPump();
            StopRxPump();
            _transport = null;
            PortName = null;
            try { await transport.DisconnectAsync(); } catch { }
            throw;
        }

        IsConnected = true;
        ConnectionChanged?.Invoke();
        MeshtasticWin.AppState.SetConnectedNodeIdHex(null);

        try
        {
            var helloMsg = ToRadioFactory.CreateHelloRequest(1u);
            var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)helloMsg);
            await transport.SendAsync(framed);
            logToUi("Sent ToRadio: WantConfigId=1");
        }
        catch (Exception ex)
        {
            logToUi($"Failed to send ToRadio hello: {ex.Message}");
        }

        StartHeartbeatPump(transport);
    }

    private void StartRxPump(Action<Action> runOnUi, Action<string> logToUi)
    {
        StopRxPump();

        lock (_rxQueueLock)
            _rxQueue.Clear();

        _rxPumpCts = new CancellationTokenSource();
        var ct = _rxPumpCts.Token;
        _rxPumpTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disconnecting) == 0)
            {
                try
                {
                    await _rxQueueSignal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                byte[]? bytes = null;
                lock (_rxQueueLock)
                {
                    if (_rxQueue.Count > 0)
                        bytes = _rxQueue.Dequeue();
                }

                if (bytes is null)
                    continue;

                ProcessIncomingBytes(bytes, runOnUi, logToUi);
            }
        }, ct);
    }

    private void StopRxPump()
    {
        var cts = _rxPumpCts;
        var task = _rxPumpTask;
        _rxPumpCts = null;
        _rxPumpTask = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        try { _rxQueueSignal.Release(); } catch { }

        if (task is not null)
        {
            try { task.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        if (cts is not null)
        {
            try { cts.Dispose(); } catch { }
        }

        lock (_rxQueueLock)
            _rxQueue.Clear();
    }

    private void StartHeartbeatPump(IRadioTransport transport)
    {
        var shouldHeartbeat = transport is SerialTransport || transport is TcpTransport;
        if (!shouldHeartbeat)
            return;

        StopHeartbeatPump();
        _heartbeatNonce = 0;

        _heartbeatCts = new CancellationTokenSource();
        var ct = _heartbeatCts.Token;

        _heartbeatTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disconnecting) == 0 && IsConnected)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (ct.IsCancellationRequested || Volatile.Read(ref _disconnecting) != 0 || !IsConnected)
                    break;

                try
                {
                    var nonce = unchecked(++_heartbeatNonce);
                    var msg = ToRadioFactory.CreateHeartbeat(nonce);
                    var framed = MeshtasticWire.Wrap((Google.Protobuf.IMessage)msg);
                    await transport.SendAsync(framed).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disconnecting) != 0)
                {
                    break;
                }
                catch (NullReferenceException) when (Volatile.Read(ref _disconnecting) != 0)
                {
                    break;
                }
                catch (IOException) when (Volatile.Read(ref _disconnecting) != 0)
                {
                    break;
                }
                catch
                {
                    // Ignore heartbeat errors; regular traffic should continue.
                }
            }
        }, ct);
    }

    private void StopHeartbeatPump()
    {
        var cts = _heartbeatCts;
        var task = _heartbeatTask;
        _heartbeatCts = null;
        _heartbeatTask = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        if (task is not null)
        {
            try { task.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }

        if (cts is not null)
        {
            try { cts.Dispose(); } catch { }
        }
    }

    private void ProcessIncomingBytes(byte[] bytes, Action<Action> runOnUi, Action<string> logToUi)
    {
        if (Volatile.Read(ref _disconnecting) != 0 || !IsConnected)
            return;

        foreach (var frame in _frameDecoder.Feed(bytes))
        {
            if (Volatile.Read(ref _disconnecting) != 0)
                return;

            var handled = FromRadioRouter.TryHandle(frame, runOnUi, logToUi, out var summary);
            if (!handled && ShouldLogProtoSummary(summary))
                logToUi($"PROTOBUF frame (unknown): {summary} ({frame.Length} bytes)");
        }

        foreach (var line in _decoder.Feed(bytes))
        {
            if (Volatile.Read(ref _disconnecting) != 0)
                return;

            // Capture packet-to-destination metadata even for lines we don't show in UI.
            try { MeshDebugLineParser.CapturePacketMetadata(line); } catch { }

            if (!LooksLikeDebugText(line))
                continue;

            // Keep serial processing lightweight: only forward high-value lines to UI/parser.
            if (!ShouldForwardTextLine(line))
                continue;

            logToUi($"TXT: {line}");

            runOnUi(() =>
            {
                try { MeshDebugLineParser.Consume(line); }
                catch (Exception ex) { logToUi($"MeshDebugLineParser error: {ex.Message}"); }
            });
        }
    }

    // Broadcast
    public async System.Threading.Tasks.Task<uint> SendTextAsync(string text)
        => await SendTextAsync(text, (uint?)null);

    // DM when toNodeNum has a value.
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

        // Keep ACK enabled for broadcast too. Not all nodes ACK broadcast, but when they do we can show it.
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

    private static bool ShouldForwardTextLine(string line)
    {
        var s = line.TrimStart();
        if (s.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Contains("Received text msg", StringComparison.OrdinalIgnoreCase)) return true;
            if (line.Contains("ToPhone queue is full", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        if (line.Contains("ToPhone queue is full", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Received text msg", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static bool ShouldLogProtoSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return false;

        var s = summary.Trim();
        if (s.Equals("other", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
