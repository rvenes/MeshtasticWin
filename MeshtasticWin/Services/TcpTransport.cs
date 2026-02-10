using Meshtastic.Core;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MeshtasticWin.Services;

public sealed class TcpTransport : IRadioTransport
{
    private readonly string _host;
    private readonly int _portNumber;
    private readonly object _sync = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;
    private int _isDisconnecting;

    public event Action<string>? Log;
    public event Action<byte[]>? BytesReceived;

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public TcpTransport(string host, int portNumber)
    {
        _host = host;
        _portNumber = portNumber;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return;

        Interlocked.Exchange(ref _isDisconnecting, 0);

        var client = new TcpClient();
        await client.ConnectAsync(_host, _portNumber, ct).ConfigureAwait(false);
        var stream = client.GetStream();

        lock (_sync)
        {
            _client = client;
            _stream = stream;
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(stream, _receiveCts.Token));
        }

        Log?.Invoke($"Connected to TCP {_host}:{_portNumber}");
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? receiveCts;
        Task? receiveLoopTask;
        NetworkStream? stream;
        TcpClient? client;

        lock (_sync)
        {
            receiveCts = _receiveCts;
            receiveLoopTask = _receiveLoopTask;
            stream = _stream;
            client = _client;

            _receiveCts = null;
            _receiveLoopTask = null;
            _stream = null;
            _client = null;
        }

        if (client is null)
            return;

        Interlocked.Exchange(ref _isDisconnecting, 1);

        try { receiveCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        if (receiveLoopTask is not null)
        {
            try { await receiveLoopTask.ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) { }
            catch (IOException) { }
        }

        try { stream?.Dispose(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try { client.Dispose(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try { receiveCts?.Dispose(); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        Log?.Invoke("Disconnected");
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        var stream = _stream;
        if (stream is null || Volatile.Read(ref _isDisconnecting) != 0)
            throw new InvalidOperationException("Not connected");

        try
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _isDisconnecting) != 0) { return; }
        catch (NullReferenceException) when (Volatile.Read(ref _isDisconnecting) != 0) { return; }
        catch (IOException) when (Volatile.Read(ref _isDisconnecting) != 0) { return; }

        Log?.Invoke($"TX {data.Length} bytes");
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && Volatile.Read(ref _isDisconnecting) == 0)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (NullReferenceException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (n <= 0)
                break;

            if (Volatile.Read(ref _isDisconnecting) != 0)
                break;

            var payload = new byte[n];
            Buffer.BlockCopy(buffer, 0, payload, 0, n);
            BytesReceived?.Invoke(payload);
            Log?.Invoke($"RX {n} bytes");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
