using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeshtasticWin.Services;
using System;
using System.IO.Ports;
using System.Linq;
using Windows.Storage;

namespace MeshtasticWin.Pages;

public sealed partial class ConnectPage : Page
{
    private const string PortSettingKey = "LastSerialPort";
    private bool _handlersHooked;
    private bool _hasPorts;

    public ConnectPage()
    {
        InitializeComponent();

        // Bind loggliste som lever i RadioClient (overlever fane-byting)
        LogList.ItemsSource = RadioClient.Instance.LogLines;

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
        UpdateUiFromClient();
        base.OnNavigatedTo(e);
    }

    private void OnConnectionChanged()
    {
        _ = DispatcherQueue.TryEnqueue(UpdateUiFromClient);
    }

    private void AddLogLineUi(string line)
    {
        // Oppdater ObservableCollection på UI-tråd
        RadioClient.Instance.AddLogFromUiThread(line);
    }

    private void LogToUi(string line)
    {
        // Kallast frå transport-trådar → må via Dispatcher.
        _ = DispatcherQueue.TryEnqueue(() => AddLogLineUi(line));
    }

    private void UpdateUiFromClient()
    {
        var client = RadioClient.Instance;

        StatusText.Text = client.IsConnected
            ? $"Connected to {client.PortName}"
            : "Disconnected";

        ConnectButton.IsEnabled = !client.IsConnected && _hasPorts;
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

            await RadioClient.Instance.ConnectAsync(
                port,
                a => DispatcherQueue.TryEnqueue(() => a()),
                LogToUi);

            SaveSelectedPort(port);
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

    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortCombo.SelectedItem is string port)
            SaveSelectedPort(port);
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

        var savedPort = LoadSelectedPort();
        var initialPort = !string.IsNullOrWhiteSpace(savedPort) && sorted.Contains(savedPort, StringComparer.OrdinalIgnoreCase)
            ? sorted.First(p => string.Equals(p, savedPort, StringComparison.OrdinalIgnoreCase))
            : sorted[0];

        PortCombo.SelectedItem = initialPort;
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

    private static string? LoadSelectedPort()
    {
        var settings = ApplicationData.Current.LocalSettings.Values;
        return settings.TryGetValue(PortSettingKey, out var value) ? value as string : null;
    }

    private static void SaveSelectedPort(string port)
    {
        ApplicationData.Current.LocalSettings.Values[PortSettingKey] = port;
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
}
