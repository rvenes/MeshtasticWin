using AppDataPaths = MeshtasticWin.Services.AppDataPaths;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeshtasticWin.Services;
using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
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

    public ConnectPage()
    {
        InitializeComponent();

        LogList.ItemsSource = RadioClient.Instance.LogLines;

        TcpHostBox.Text = LoadSetting(TcpHostSettingKey) ?? "127.0.0.1";
        TcpPortBox.Text = LoadSetting(TcpPortSettingKey) ?? "4403";

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
        BluetoothConnectButton.IsEnabled = !client.IsConnected && _hasBluetoothDevices;
        DisconnectButton.IsEnabled = client.IsConnected;
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

            SaveSetting(PortSettingKey, port);
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

            SaveSetting(TcpHostSettingKey, host);
            SaveSetting(TcpPortSettingKey, port.ToString());
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

            await RadioClient.Instance.ConnectBluetoothAsync(option.DeviceId, option.DisplayName, RunOnUi(), LogToUi);

            SaveSetting(BluetoothDeviceIdSettingKey, option.DeviceId);
            UpdateUiFromClient();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            AddLogLineUi(ex.Message);
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
            SaveSetting(PortSettingKey, port);
    }

    private void BluetoothCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BluetoothCombo.SelectedItem is BluetoothDeviceOption option)
            SaveSetting(BluetoothDeviceIdSettingKey, option.DeviceId);
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

        var savedPort = LoadSetting(PortSettingKey);
        var initialPort = !string.IsNullOrWhiteSpace(savedPort) && sorted.Contains(savedPort, StringComparer.OrdinalIgnoreCase)
            ? sorted.First(p => string.Equals(p, savedPort, StringComparison.OrdinalIgnoreCase))
            : sorted[0];

        PortCombo.SelectedItem = initialPort;
    }

    private async Task RefreshBluetoothDevicesAsync()
    {
        try
        {
            BluetoothCombo.Items.Clear();

            var selector = BluetoothLEDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);

            var options = devices
                .Select(d => new BluetoothDeviceOption(
                    d.Id,
                    string.IsNullOrWhiteSpace(d.Name) ? "(Unnamed device)" : d.Name))
                .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
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
                return;
            }

            foreach (var option in options)
                BluetoothCombo.Items.Add(option);

            BluetoothCombo.IsEnabled = true;
            _hasBluetoothDevices = true;

            var savedDeviceId = LoadSetting(BluetoothDeviceIdSettingKey);
            var initial = !string.IsNullOrWhiteSpace(savedDeviceId)
                ? options.FirstOrDefault(o => string.Equals(o.DeviceId, savedDeviceId, StringComparison.OrdinalIgnoreCase)) ?? options[0]
                : options[0];

            BluetoothCombo.SelectedItem = initial;
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
        }
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

    private static string? LoadSetting(string key)
    {
        var settings = ApplicationData.Current.LocalSettings.Values;
        return settings.TryGetValue(key, out var value) ? value as string : null;
    }

    private static void SaveSetting(string key, string value)
    {
        ApplicationData.Current.LocalSettings.Values[key] = value;
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
            var debugDir = Path.Combine(@"H:\Koding\MeshtasticWin\Debuglogg", AppDataPaths.ActiveNodeScope);
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
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    private sealed record BluetoothDeviceOption(string DeviceId, string DisplayName);
}
