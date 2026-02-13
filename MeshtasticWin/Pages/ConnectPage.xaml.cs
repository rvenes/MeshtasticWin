using AppDataPaths = MeshtasticWin.Services.AppDataPaths;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeshtasticWin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Storage;

namespace MeshtasticWin.Pages;

public sealed partial class ConnectPage : Page
{
    private const string PortSettingKey = "LastSerialPort";
    private const string TcpHostSettingKey = "LastTcpHost";
    private const string TcpPortSettingKey = "LastTcpPort";
    private const string BluetoothDeviceIdSettingKey = "LastBluetoothDeviceId";

    private bool _handlersHooked;
    private bool _hasPorts;
    private bool _hasBluetoothDevices;
    private bool _isBluetoothScanning;

    public ConnectPage()
    {
        InitializeComponent();

        LogList.ItemsSource = RadioClient.Instance.LogLines;

        TcpHostBox.Text = SettingsStore.GetString(TcpHostSettingKey) ?? "127.0.0.1";
        TcpPortBox.Text = SettingsStore.GetString(TcpPortSettingKey) ?? "4403";

        HookClientEvents();
        UpdateUiFromClient();
    }

    private void HookClientEvents()
    {
        if (_handlersHooked)
            return;

        _handlersHooked = true;
        RadioClient.Instance.ConnectionChanged += OnConnectionChanged;
    }

    private void UnhookClientEvents()
    {
        if (!_handlersHooked)
            return;

        _handlersHooked = false;
        RadioClient.Instance.ConnectionChanged -= OnConnectionChanged;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        UnhookClientEvents();
        base.OnNavigatedFrom(e);
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        HookClientEvents();
        RefreshPorts();
        _ = RefreshBluetoothDevicesAsync();
        UpdateUiFromClient();
        base.OnNavigatedTo(e);
    }

    private void OnConnectionChanged()
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq is not null)
                _ = dq.TryEnqueue(UpdateUiFromClient);
        }
        catch
        {
            // Page/window may be shutting down.
        }
    }

    private void AddLogLineUi(string line)
    {
        RadioClient.Instance.AddLogFromUiThread(line);
    }

    private void LogToUi(string line)
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq is not null)
                _ = dq.TryEnqueue(() => AddLogLineUi(line));
        }
        catch
        {
            // Ignore late callbacks during app shutdown.
        }
    }

    private Action<Action> RunOnUi()
        => a => DispatcherQueue.TryEnqueue(() => a());

    private void UpdateUiFromClient()
    {
        var client = RadioClient.Instance;

        StatusText.Text = client.IsConnected
            ? $"Connected to {client.PortName}"
            : "Disconnected";

        ConnectButton.IsEnabled = !client.IsConnected && _hasPorts;
        TcpConnectButton.IsEnabled = !client.IsConnected;
        BluetoothConnectButton.IsEnabled = !client.IsConnected && _hasBluetoothDevices && !_isBluetoothScanning;
        DisconnectButton.IsEnabled = client.IsConnected;

        RefreshBluetoothButton.IsEnabled = !_isBluetoothScanning;
        BluetoothScanRing.IsActive = _isBluetoothScanning;
        BluetoothScanRing.Visibility = _isBluetoothScanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var port = PortCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(port))
            {
                AddLogLineUi("No serial port selected.");
                UpdateUiFromClient();
                return;
            }

            if (RadioClient.Instance.IsConnected)
            {
                AddLogLineUi("Already connected.");
                UpdateUiFromClient();
                return;
            }

            await RadioClient.Instance.ConnectAsync(port, RunOnUi(), LogToUi);

            SettingsStore.SetString(PortSettingKey, port);
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
            UpdateUiFromClient();
        }
    }

    private async void TcpConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var host = TcpHostBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                AddLogLineUi("TCP host is required.");
                UpdateUiFromClient();
                return;
            }

            if (!int.TryParse(TcpPortBox.Text?.Trim(), out var port) || port < 1 || port > 65535)
            {
                AddLogLineUi("TCP port must be 1-65535.");
                UpdateUiFromClient();
                return;
            }

            if (RadioClient.Instance.IsConnected)
            {
                AddLogLineUi("Already connected.");
                UpdateUiFromClient();
                return;
            }

            await RadioClient.Instance.ConnectTcpAsync(host, port, RunOnUi(), LogToUi);

            SettingsStore.SetString(TcpHostSettingKey, host);
            SettingsStore.SetString(TcpPortSettingKey, port.ToString());
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
            UpdateUiFromClient();
        }
    }

    private async void BluetoothConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BluetoothCombo.SelectedItem is not BluetoothDeviceOption option)
            {
                AddLogLineUi("No Bluetooth device selected.");
                UpdateUiFromClient();
                return;
            }

            if (RadioClient.Instance.IsConnected)
            {
                AddLogLineUi("Already connected.");
                UpdateUiFromClient();
                return;
            }

            AddLogLineUi($"Connecting to Bluetooth {option.DisplayName}...");
            await RadioClient.Instance.ConnectBluetoothAsync(option.DeviceId, option.DisplayName, RunOnUi(), LogToUi);

            SettingsStore.SetString(BluetoothDeviceIdSettingKey, option.DeviceId);
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi($"Bluetooth connect failed: {ex.Message}");
            UpdateUiFromClient();
        }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshPorts();
        UpdateUiFromClient();
    }

    private async void RefreshBluetooth_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBluetoothDevicesAsync();
        UpdateUiFromClient();
    }

    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortCombo.SelectedItem is string port)
            SettingsStore.SetString(PortSettingKey, port);
    }

    private void BluetoothCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BluetoothCombo.SelectedItem is BluetoothDeviceOption option)
            SettingsStore.SetString(BluetoothDeviceIdSettingKey, option.DeviceId);
    }

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames();
        var sorted = ports
            .OrderBy(GetPortSortKey)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PortCombo.Items.Clear();

        if (sorted.Count == 0)
        {
            var emptyItem = new ComboBoxItem
            {
                Content = "(no ports found)",
                IsEnabled = false
            };

            PortCombo.Items.Add(emptyItem);
            PortCombo.SelectedIndex = 0;
            PortCombo.IsEnabled = false;
            _hasPorts = false;
            return;
        }

        foreach (var port in sorted)
            PortCombo.Items.Add(port);

        PortCombo.IsEnabled = true;
        _hasPorts = true;

        var savedPort = SettingsStore.GetString(PortSettingKey);
        var initialPort = !string.IsNullOrWhiteSpace(savedPort) && sorted.Contains(savedPort, StringComparer.OrdinalIgnoreCase)
            ? sorted.First(p => string.Equals(p, savedPort, StringComparison.OrdinalIgnoreCase))
            : sorted[0];

        PortCombo.SelectedItem = initialPort;
    }

    private async Task RefreshBluetoothDevicesAsync()
    {
        try
        {
            _isBluetoothScanning = true;
            BluetoothCombo.Items.Clear();
            BluetoothCombo.Items.Add(new ComboBoxItem
            {
                Content = "(scanning...)",
                IsEnabled = false
            });
            BluetoothCombo.SelectedIndex = 0;
            BluetoothCombo.IsEnabled = false;
            _hasBluetoothDevices = false;
            UpdateUiFromClient();

            var devices = await ScanBluetoothDevicesAsync();

            var options = devices
                .Select(d => new BluetoothDeviceOption(
                    d.Id,
                    BuildBluetoothDisplayName(d)))
                .OrderBy(d => GetBluetoothSortKey(d.DisplayName))
                .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (options.Count == 0)
            {
                BluetoothCombo.Items.Add(new ComboBoxItem
                {
                    Content = "(no bluetooth devices found)",
                    IsEnabled = false
                });
                BluetoothCombo.SelectedIndex = 0;
                BluetoothCombo.IsEnabled = false;
                _hasBluetoothDevices = false;
                UpdateUiFromClient();
                return;
            }

            foreach (var option in options)
                BluetoothCombo.Items.Add(option);

            BluetoothCombo.IsEnabled = true;
            _hasBluetoothDevices = true;

            var savedDeviceId = SettingsStore.GetString(BluetoothDeviceIdSettingKey);
            var initial = !string.IsNullOrWhiteSpace(savedDeviceId)
                ? options.FirstOrDefault(o => string.Equals(o.DeviceId, savedDeviceId, StringComparison.OrdinalIgnoreCase)) ?? options[0]
                : options[0];

            BluetoothCombo.SelectedItem = initial;
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            BluetoothCombo.Items.Clear();
            BluetoothCombo.Items.Add(new ComboBoxItem
            {
                Content = "(bluetooth unavailable)",
                IsEnabled = false
            });
            BluetoothCombo.SelectedIndex = 0;
            BluetoothCombo.IsEnabled = false;
            _hasBluetoothDevices = false;
            AddLogLineUi($"Bluetooth scan failed: {ex.Message}");
            UpdateUiFromClient();
        }
        finally
        {
            _isBluetoothScanning = false;
            UpdateUiFromClient();
        }
    }

    private static async Task<IReadOnlyList<DeviceInformation>> ScanBluetoothDevicesAsync()
    {
        const string BleProtocolAqs = "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
        var perSelectorTimeout = TimeSpan.FromSeconds(4);

        var selectors = new[]
        {
            BluetoothLEDevice.GetDeviceSelector(),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
            BleProtocolAqs
        };

        var byId = new Dictionary<string, DeviceInformation>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<Exception>();
        var results = await Task.WhenAll(selectors
            .Distinct(StringComparer.Ordinal)
            .Select(selector => ScanSelectorAsync(selector, perSelectorTimeout)));
        var anySelectorSucceeded = false;
        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                errors.Add(result.Error);
                continue;
            }

            if (result.TimedOut)
                continue;

            anySelectorSucceeded = true;

            foreach (var d in result.Devices)
            {
                if (string.IsNullOrWhiteSpace(d.Id))
                    continue;

                if (!byId.TryGetValue(d.Id, out var existing))
                {
                    byId[d.Id] = d;
                    continue;
                }

                // Prefer entries with a readable name.
                if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(d.Name))
                    byId[d.Id] = d;
            }
        }

        if (!anySelectorSucceeded && errors.Count > 0)
            throw new InvalidOperationException($"Bluetooth scan failed: {errors[0].Message}", errors[0]);

        return byId.Values.ToList();
    }

    private static async Task<BluetoothSelectorScanResult> ScanSelectorAsync(string selector, TimeSpan timeout)
    {
        try
        {
            var queryTask = DeviceInformation.FindAllAsync(selector).AsTask();
            var completedTask = await Task.WhenAny(queryTask, Task.Delay(timeout));
            if (completedTask != queryTask)
            {
                // Consume potential late exceptions from timed out discovery task.
                _ = queryTask.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return new BluetoothSelectorScanResult(Array.Empty<DeviceInformation>(), null, true);
            }

            return new BluetoothSelectorScanResult(await queryTask, null, false);
        }
        catch (Exception ex)
        {
            return new BluetoothSelectorScanResult(Array.Empty<DeviceInformation>(), ex, false);
        }
    }

    private static string BuildBluetoothDisplayName(DeviceInformation device)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? "(Unnamed device)" : device.Name.Trim();
        var isPaired = device.Pairing?.IsPaired == true;
        return isPaired ? $"{name} [paired]" : name;
    }

    private static (int group, string name) GetBluetoothSortKey(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName) &&
            displayName.IndexOf("meshtastic", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return (0, displayName);
        }

        return (1, displayName ?? "");
    }

    private static (int group, int number, string name) GetPortSortKey(string port)
    {
        if (!string.IsNullOrWhiteSpace(port) &&
            port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(port[3..], out var number))
        {
            return (0, number, port);
        }

        return (1, int.MaxValue, port ?? "");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RadioClient.Instance.DisconnectAsync();
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
            UpdateUiFromClient();
        }
    }

    private void SaveDebugLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var debugDir = Path.Combine(AppDataPaths.DebugLogsRootPath, AppDataPaths.ActiveNodeScope);
            Directory.CreateDirectory(debugDir);

            var fileName = $"connect_debug_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            var path = Path.Combine(debugDir, fileName);

            var snapshot = RadioClient.Instance.LogLines.ToArray();
            File.WriteAllLines(path, snapshot);

            AddLogLineUi($"Debug log saved: {path}");
        }
        catch (Exception ex)
        {
            AddLogLineUi($"Failed to save debug log: {ex.Message}");
        }
    }

    private void LogList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is string line)
            LogList.SelectedItem = line;
    }

    private void LogListFlyout_Opening(object sender, object e)
    {
        CopyLogMenuItem.IsEnabled = LogList.SelectedItem is string;
    }

    private void CopyLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not string line || string.IsNullOrWhiteSpace(line))
            return;

        CopyTextToClipboard(line);
    }

    private void CopyAllLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var lines = RadioClient.Instance.LogLines;
        if (lines.Count == 0)
            return;

        CopyTextToClipboard(string.Join(Environment.NewLine, lines));
    }

    private static void CopyTextToClipboard(string text)
    {
        _ = ClipboardUtil.TrySetText(text, flush: true);
    }

    private sealed record BluetoothDeviceOption(string DeviceId, string DisplayName);
    private sealed record BluetoothSelectorScanResult(IReadOnlyList<DeviceInformation> Devices, Exception? Error, bool TimedOut);
}
