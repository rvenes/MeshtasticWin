using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeshtasticWin.Models;
using MeshtasticWin.Services;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.System;

namespace MeshtasticWin.Pages;

public sealed partial class NodesPage : Page, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NodeLive> FilteredNodesView { get; } = new();

    private int _hideOlderThanDays = 90; // default: 3 months
    private bool _hideInactive = true;
    private string _filter = "";
    private readonly DispatcherTimer _throttle = new();
    private bool _mapReady;
    private readonly DispatcherTimer _traceRouteTimer = new();
    private int _traceRouteRemainingSeconds;
    private bool _traceRouteCooldownActive;

    private NodeLive? _selected;
    public NodeLive? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;

            OnChanged(nameof(Selected));
            OnChanged(nameof(HasSelection));
            OnChanged(nameof(SelectedTitle));
            OnChanged(nameof(SelectedIdHex));
            OnChanged(nameof(SelectedNodeNumText));
            OnChanged(nameof(SelectedLastHeardText));
            OnChanged(nameof(SelectedPosText));
            OnChanged(nameof(SelectedExtraText));
            OnChanged(nameof(IsTraceRouteEnabled));
            OnChanged(nameof(TraceRouteButtonText));

            _ = PushSelectionToMapAsync();
        }
    }

    public bool HasSelection => Selected is not null;

    public string SelectedTitle => Selected?.Name ?? "Select a node";
    public string SelectedIdHex => Selected?.IdHex ?? "—";
    public string SelectedNodeNumText => Selected is null ? "—" : $"nodeNum: {Selected.NodeNum}";
    public string SelectedLastHeardText => Selected is null ? "—" : $"last heard: {Selected.LastHeard}";
    public string SelectedPosText
    {
        get
        {
            if (Selected is null) return "position: —";
            if (!Selected.HasPosition) return "position: —";
            return $"position: {Selected.Latitude:0.000000},{Selected.Longitude:0.000000} ({Selected.LastPositionText})";
        }
    }
    public string SelectedExtraText => Selected is null ? "" : $"Long name: {Selected.LongName}   Short name: {Selected.ShortName}";

    public bool IsTraceRouteEnabled => HasSelection && !_traceRouteCooldownActive;

    public string TraceRouteButtonText =>
        _traceRouteCooldownActive
            ? $"Trace Route ({_traceRouteRemainingSeconds}s)"
            : "Trace Route";

    public string NodeCountsText
    {
        get
        {
            var total = MeshtasticWin.AppState.Nodes.Count;
            var online = MeshtasticWin.AppState.Nodes.Count(n => IsOnlineByRssi(n));
            return $"Online: {online}   Total: {total}";
        }
    }

    public NodesPage()
    {
        InitializeComponent();

        AgeFilterCombo.Items.Add("Show all");
        AgeFilterCombo.Items.Add("Hide > 1 week");
        AgeFilterCombo.Items.Add("Hide > 2 weeks");
        AgeFilterCombo.Items.Add("Hide > 3 weeks");
        AgeFilterCombo.Items.Add("Hide > 4 weeks");
        AgeFilterCombo.Items.Add("Hide > 1 month");
        AgeFilterCombo.Items.Add("Hide > 3 months");
        AgeFilterCombo.SelectedIndex = 6;

        HideInactiveToggle.IsChecked = _hideInactive;

        MeshtasticWin.AppState.Nodes.CollectionChanged += Nodes_CollectionChanged;
        foreach (var n in MeshtasticWin.AppState.Nodes)
            n.PropertyChanged += Node_PropertyChanged;

        RebuildFiltered();

        _throttle.Interval = TimeSpan.FromMilliseconds(350);
        _throttle.Tick += (_, __) => { _throttle.Stop(); _ = PushAllNodesToMapAsync(); };

        _traceRouteTimer.Interval = TimeSpan.FromSeconds(1);
        _traceRouteTimer.Tick += (_, __) => TraceRouteCooldownTick();

        Loaded += NodesPage_Loaded;
        Unloaded += NodesPage_Unloaded;
    }

    private void NodesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        MeshtasticWin.AppState.Nodes.CollectionChanged -= Nodes_CollectionChanged;
        foreach (var n in MeshtasticWin.AppState.Nodes)
            n.PropertyChanged -= Node_PropertyChanged;
    }

    private async void NodesPage_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureMapAsync();
        await PushAllNodesToMapAsync();
        await PushSelectionToMapAsync();
    }

    private async System.Threading.Tasks.Task EnsureMapAsync()
    {
        if (MapView.CoreWebView2 is not null)
            return;

        await MapView.EnsureCoreWebView2Async();
        var wv = MapView.CoreWebView2;
        if (wv is null) return;

        wv.WebMessageReceived += CoreWebView2_WebMessageReceived;

        var installPath = Package.Current.InstalledLocation.Path;
        var mapFolder = Path.Combine(installPath, "Assets", "Map");

        wv.SetVirtualHostNameToFolderMapping("appassets.local", mapFolder, CoreWebView2HostResourceAccessKind.Allow);
        MapView.Source = new Uri("https://appassets.local/map.html");
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(type)) return;

            if (type == "ready")
            {
                _mapReady = true;
                _ = PushAllNodesToMapAsync();
                _ = PushSelectionToMapAsync();
                return;
            }

            if (type == "selectNode")
            {
                var id = root.TryGetProperty("idHex", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) return;

                var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n => n.IdHex.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (node is not null)
                {
                    NodesList.SelectedItem = node;
                    Selected = node;

                    // Scroll slik at den blir synleg
                    NodesList.ScrollIntoView(node);
                }
                return;
            }

            if (type == "requestHistory")
            {
                var id = root.TryGetProperty("idHex", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) return;

                if (!GpsArchive.HasLog(id)) return;

                var points = GpsArchive.ReadAll(id, maxPoints: 5000);
                var payload = new
                {
                    type = "history",
                    idHex = id,
                    points = points.Select(p => new { lat = p.Lat, lon = p.Lon, tsUtc = p.TsUtc.ToString("o") })
                };
                MapView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
        }
        catch { }
    }

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (NodeLive n in e.NewItems) n.PropertyChanged += Node_PropertyChanged;

        if (e.OldItems is not null)
            foreach (NodeLive n in e.OldItems) n.PropertyChanged -= Node_PropertyChanged;

        RebuildFiltered();
        OnChanged(nameof(NodeCountsText));
        TriggerMapUpdate();
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Selected))
        {
            OnChanged(nameof(SelectedLastHeardText));
            OnChanged(nameof(SelectedPosText));
            OnChanged(nameof(SelectedExtraText));
            OnChanged(nameof(SelectedTitle));
        }

        if (e.PropertyName is nameof(NodeLive.LastHeardUtc) or nameof(NodeLive.LastHeard)
            or nameof(NodeLive.Latitude) or nameof(NodeLive.Longitude)
            or nameof(NodeLive.RSSI) or nameof(NodeLive.SNR))
        {
            OnChanged(nameof(NodeCountsText));
            RebuildFiltered();
            TriggerMapUpdate();
        }
    }

    private bool IsOnlineByRssi(NodeLive n)
    {
        // Online = har målt RSSI (ikkje "—" og ikkje 0)
        if (string.IsNullOrWhiteSpace(n.RSSI) || n.RSSI == "—") return false;
        if (int.TryParse(n.RSSI, out var rssi))
            return rssi != 0;
        return false;
    }

    private bool IsTooOld(NodeLive n)
    {
        if (_hideOlderThanDays >= 99999) return false;
        if (n.LastHeardUtc == DateTime.MinValue) return true;
        return (DateTime.UtcNow - n.LastHeardUtc) > TimeSpan.FromDays(_hideOlderThanDays);
    }

    private bool IsHiddenByInactive(NodeLive n)
    {
        if (!_hideInactive) return false;
        return !IsOnlineByRssi(n);
    }

    private void TriggerMapUpdate()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        _throttle.Stop();
        _throttle.Start();
    }

    private void RebuildFiltered()
    {
        FilteredNodesView.Clear();

        var q = (_filter ?? "").Trim();

        var nodes = MeshtasticWin.AppState.Nodes
            .Where(n => !IsTooOld(n))
            .Where(n => !IsHiddenByInactive(n))
            .Where(n =>
            {
                if (string.IsNullOrWhiteSpace(q)) return true;
                return (n.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (n.IdHex?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (n.ShortId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .OrderByDescending(n => IsOnlineByRssi(n))
            .ThenByDescending(n => n.LastHeardUtc)
            .ThenBy(n => n.Name)
            .ToList();

        foreach (var n in nodes)
            FilteredNodesView.Add(n);

        if (Selected is null && FilteredNodesView.Count > 0)
        {
            NodesList.SelectedItem = FilteredNodesView[0];
            Selected = FilteredNodesView[0];
        }

        OnChanged(nameof(NodeCountsText));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        RebuildFiltered();
        TriggerMapUpdate();
    }

    private void AgeFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _hideOlderThanDays = AgeFilterCombo.SelectedIndex switch
        {
            0 => 99999,
            1 => 7,
            2 => 14,
            3 => 21,
            4 => 28,
            5 => 31,
            6 => 90,
            _ => 99999
        };

        RebuildFiltered();
        TriggerMapUpdate();
    }

    private void HideInactiveToggle_Click(object sender, RoutedEventArgs e)
    {
        _hideInactive = HideInactiveToggle.IsChecked == true;
        RebuildFiltered();
        TriggerMapUpdate();
    }

    private void NodesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodesList.SelectedItem is NodeLive node)
            Selected = node;
    }

    private void NodesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        ZoomToNode_Click(sender, e);
    }

    private async System.Threading.Tasks.Task PushAllNodesToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;

        var nodes = MeshtasticWin.AppState.Nodes
            .Where(n => !IsTooOld(n))
            .Where(n => !IsHiddenByInactive(n))
            .Select(n => new
            {
                idHex = n.IdHex,
                name = n.Name,
                shortName = n.ShortName,
                shortId = n.ShortId,
                snr = n.SNR,
                rssi = n.RSSI,
                lastHeard = n.LastHeard,
                lat = n.Latitude,
                lon = n.Longitude,
                hasPos = n.HasPosition,
                lastPosLocal = n.LastPositionText
            })
            .ToArray();

        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nodes", nodes }));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task PushSelectionToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "selected", idHex = Selected?.IdHex }));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void FitAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"fitAll\"}");
    }

    private void ZoomToNode_Click(object sender, RoutedEventArgs e)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        if (Selected is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "zoomTo", idHex = Selected.IdHex }));
    }

    private void ReadGpsLog_Click(object sender, RoutedEventArgs e)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        if (Selected is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "requestHistory", idHex = Selected.IdHex }));
    }

    private void SendDm_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        MeshtasticWin.AppState.SetActiveChatPeer(Selected.IdHex);
        App.MainWindowInstance?.NavigateTo("messages");
    }

    private async void DeviceMetricsLog_Click(object sender, RoutedEventArgs e)
        => await ShowNodeLogAsync(NodeLogType.DeviceMetrics, "Device Metrics Log");

    private async void EnvironmentMetricsLog_Click(object sender, RoutedEventArgs e)
        => await ShowNodeLogAsync(NodeLogType.EnvironmentMetrics, "Environment Metrics Log");

    private async void PowerMetricsLog_Click(object sender, RoutedEventArgs e)
        => await ShowNodeLogAsync(NodeLogType.PowerMetrics, "Power Metrics Log");

    private async void DetectionSensorLog_Click(object sender, RoutedEventArgs e)
        => await ShowNodeLogAsync(NodeLogType.DetectionSensor, "Detection Sensor Log");

    private async void TraceRouteLog_Click(object sender, RoutedEventArgs e)
        => await ShowNodeLogAsync(NodeLogType.TraceRoute, "Trace Route Log");

    private async void PositionLog_Click(object sender, RoutedEventArgs e)
        => await ShowPositionLogAsync();

    private async void ExchangeUserInfo_Click(object sender, RoutedEventArgs e)
        => await SendRequestAsync("Exchange User Info", async nodeNum =>
            await RadioClient.Instance.SendNodeInfoRequestAsync(nodeNum));

    private async void ExchangePositions_Click(object sender, RoutedEventArgs e)
        => await SendRequestAsync("Exchange Positions", async nodeNum =>
            await RadioClient.Instance.SendPositionRequestAsync(nodeNum));

    private async void TraceRoute_Click(object sender, RoutedEventArgs e)
        => await SendTraceRouteAsync();

    private async System.Threading.Tasks.Task ShowNodeLogAsync(NodeLogType type, string title)
    {
        if (Selected is null) return;

        var lines = NodeLogArchive.ReadTail(type, Selected.IdHex, maxLines: 400);
        var content = lines.Length == 0 ? "No log entries yet." : string.Join(Environment.NewLine, lines);

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = "Close",
            XamlRoot = XamlRoot,
            Content = new ScrollViewer
            {
                Content = new TextBox
                {
                    Text = content,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    MinHeight = 200
                }
            }
        };

        await dialog.ShowAsync();
    }

    private async System.Threading.Tasks.Task ShowPositionLogAsync()
    {
        if (Selected is null) return;

        var points = GpsArchive.ReadAll(Selected.IdHex, maxPoints: 5000)
            .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = points
            .Select(p => PositionLogEntry.FromPoint(p))
            .ToList();

        var window = new Window
        {
            Title = "Position Log"
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = entries,
            MinHeight = 260
        };
        listView.ItemTemplate = BuildPositionLogTemplate();

        PositionLogEntry? selectedEntry = null;

        var emptyText = new TextBlock
        {
            Text = "No position entries available.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var openMapsButton = new Button
        {
            Content = "Open in Google Maps",
            IsEnabled = false
        };
        openMapsButton.Click += async (_, __) =>
        {
            if (selectedEntry is null) return;
            var lat = selectedEntry.Lat.ToString("0.0000000", CultureInfo.InvariantCulture);
            var lon = selectedEntry.Lon.ToString("0.0000000", CultureInfo.InvariantCulture);
            var uri = new Uri($"https://www.google.com/maps/search/?api=1&query={lat},{lon}");
            await Launcher.LaunchUriAsync(uri);
        };

        var closeButton = new Button { Content = "Close" };
        closeButton.Click += (_, __) => window.Close();

        listView.SelectionChanged += (_, __) =>
        {
            selectedEntry = listView.SelectedItem as PositionLogEntry;
            openMapsButton.IsEnabled = selectedEntry is not null;
            if (selectedEntry is not null)
                ShowPositionOnMap(selectedEntry.Lat, selectedEntry.Lon);
        };

        buttonPanel.Children.Add(openMapsButton);
        buttonPanel.Children.Add(closeButton);

        Grid.SetRow(listView, 0);
        Grid.SetRow(emptyText, 0);
        Grid.SetRow(buttonPanel, 1);
        rootGrid.Children.Add(listView);
        rootGrid.Children.Add(emptyText);
        rootGrid.Children.Add(buttonPanel);

        window.Content = rootGrid;
        window.Activate();
    }

    private async System.Threading.Tasks.Task SendRequestAsync(string actionName, Func<uint, System.Threading.Tasks.Task<uint>> action)
    {
        if (Selected is null) return;
        if (Selected.NodeNum == 0)
        {
            await ShowStatusAsync($"{actionName}: nodeNum is missing.");
            return;
        }

        try
        {
            var packetId = await action((uint)Selected.NodeNum);
            RadioClient.Instance.AddLogFromUiThread($"{actionName} sent to {Selected.Name} (packetId=0x{packetId:x8}).");
            await ShowStatusAsync($"{actionName} sent to {Selected.Name}.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"{actionName} failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task ShowStatusAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "MeshtasticWin",
            PrimaryButtonText = "Close",
            XamlRoot = XamlRoot,
            Content = message
        };

        await dialog.ShowAsync();
    }

    private async System.Threading.Tasks.Task SendTraceRouteAsync()
    {
        if (Selected is null) return;
        if (_traceRouteCooldownActive) return;
        if (Selected.NodeNum == 0)
        {
            RadioClient.Instance.AddLogFromUiThread("Trace Route failed: nodeNum is missing.");
            return;
        }

        try
        {
            var packetId = await RadioClient.Instance.SendTraceRouteRequestAsync((uint)Selected.NodeNum);
            RadioClient.Instance.AddLogFromUiThread($"Trace Route sent to {Selected.Name} (packetId=0x{packetId:x8}).");
            StartTraceRouteCooldown();
        }
        catch (Exception ex)
        {
            RadioClient.Instance.AddLogFromUiThread($"Trace Route failed: {ex.Message}");
        }
    }

    private void StartTraceRouteCooldown()
    {
        _traceRouteCooldownActive = true;
        _traceRouteRemainingSeconds = 30;
        OnChanged(nameof(IsTraceRouteEnabled));
        OnChanged(nameof(TraceRouteButtonText));
        _traceRouteTimer.Start();
    }

    private void TraceRouteCooldownTick()
    {
        if (!_traceRouteCooldownActive) return;

        _traceRouteRemainingSeconds = Math.Max(0, _traceRouteRemainingSeconds - 1);
        OnChanged(nameof(TraceRouteButtonText));

        if (_traceRouteRemainingSeconds == 0)
        {
            _traceRouteCooldownActive = false;
            _traceRouteTimer.Stop();
            OnChanged(nameof(IsTraceRouteEnabled));
            OnChanged(nameof(TraceRouteButtonText));
        }
    }

    private void ShowPositionOnMap(double lat, double lon)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "positionPeek", lat, lon };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private static DataTemplate BuildPositionLogTemplate()
    {
        var xaml = @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
<TextBlock Text='{Binding DisplayText}' TextWrapping='NoWrap' FontFamily='Consolas' />
</DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private sealed record PositionLogEntry(DateTime TimestampUtc, double Lat, double Lon, double? Alt, string DisplayText)
    {
        public static PositionLogEntry FromPoint(GpsArchive.PositionPoint point)
        {
            var altText = point.Alt.HasValue
                ? $" alt={point.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture)}"
                : "";
            var lat = point.Lat.ToString("0.0000000", CultureInfo.InvariantCulture);
            var lon = point.Lon.ToString("0.0000000", CultureInfo.InvariantCulture);
            var display = $"{point.TsUtc:O} | {lat},{lon}{altText}";
            return new PositionLogEntry(point.TsUtc, point.Lat, point.Lon, point.Alt, display);
        }
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
