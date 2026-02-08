using System.IO.Ports;
using Meshtastic.Core;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace Meshtastic.Transport.Serial;

public sealed class SerialTransport : IRadioTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly object _sync = new();
    private SerialPort? _port;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;
    private int _isDisconnecting;

    public event Action<string>? Log;
    public event Action<byte[]>? BytesReceived;

    public bool IsConnected => _port?.IsOpen == true;

    public SerialTransport(string portName, int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return Task.CompletedTask;

        Interlocked.Exchange(ref _isDisconnecting, 0);

        var port = new SerialPort(_portName, _baudRate)
        {
            DtrEnable = true,
            RtsEnable = true
        };

        port.Open();

        lock (_sync)
        {
            _port = port;
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(port, _receiveCts.Token));
        }

        Log?.Invoke($"Connected to {_portName}");
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        SerialPort? port;
        CancellationTokenSource? receiveCts;
        Task? receiveLoopTask;

        lock (_sync)
        {
            port = _port;
            receiveCts = _receiveCts;
            receiveLoopTask = _receiveLoopTask;
            _port = null;
            _receiveCts = null;
            _receiveLoopTask = null;
        }

        if (port is null)
            return;

        Interlocked.Exchange(ref _isDisconnecting, 1);

        try
        {
            port.DataReceived -= OnDataReceived;
            port.ErrorReceived -= OnErrorReceived;
            port.PinChanged -= OnPinChanged;
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

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

        try
        {
            receiveCts?.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try
        {
            if (port.IsOpen)
                port.Close();
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        try
        {
            port.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
        catch (IOException) { }

        Log?.Invoke("Disconnected");
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        var port = _port;
        if (port is null || !port.IsOpen || Volatile.Read(ref _isDisconnecting) != 0)
            throw new InvalidOperationException("Not connected");

        try
        {
            port.Write(data, 0, data.Length);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _isDisconnecting) != 0) { return Task.CompletedTask; }
        catch (NullReferenceException) when (Volatile.Read(ref _isDisconnecting) != 0) { return Task.CompletedTask; }
        catch (IOException) when (Volatile.Read(ref _isDisconnecting) != 0) { return Task.CompletedTask; }

        Log?.Invoke($"TX {data.Length} bytes");
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(SerialPort port, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && Volatile.Read(ref _isDisconnecting) == 0)
        {
            int n;
            try
            {
                n = await port.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
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
                continue;

            if (Volatile.Read(ref _isDisconnecting) != 0)
                break;

            var payload = new byte[n];
            Buffer.BlockCopy(buffer, 0, payload, 0, n);

            BytesReceived?.Invoke(payload);
            Log?.Invoke($"RX {n} bytes");
        }
    }

    // Kept for safe explicit unsubscription during shutdown.
    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e) { }
    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e) { }
    private void OnPinChanged(object sender, SerialPinChangedEventArgs e) { }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
