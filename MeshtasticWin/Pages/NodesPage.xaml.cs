using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeshtasticWin.Models;
using MeshtasticWin.Services;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
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
    private readonly DispatcherTimer _logPollTimer = new();

    private readonly Dictionary<string, Dictionary<LogKind, DateTime>> _lastLogWriteByNode = new();
    private readonly Dictionary<string, HashSet<LogKind>> _pendingLogIndicatorsByNode = new();

    private bool _deviceMetricsTabIndicator;
    private bool _positionTabIndicator;
    private bool _traceRouteTabIndicator;
    private bool _powerMetricsTabIndicator;
    private bool _detectionSensorTabIndicator;

    private string _deviceMetricsLogText = "No log entries yet.";
    private string _traceRouteLogText = "No log entries yet.";
    private string _powerMetricsLogText = "No log entries yet.";
    private string _detectionSensorLogText = "No log entries yet.";

    internal ObservableCollection<PositionLogEntry> PositionLogEntries { get; } = new();
    private PositionLogEntry? _selectedPositionEntry;

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

            if (_selected is not null)
            {
                _selected.HasLogIndicator = false;
                ApplyPendingIndicatorsForSelectedNode();
                RefreshSelectedNodeLogs();
                DetailsTabs.SelectedIndex = 0;
            }

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

    public string DeviceMetricsLogText
    {
        get => _deviceMetricsLogText;
        private set
        {
            if (_deviceMetricsLogText == value) return;
            _deviceMetricsLogText = value;
            OnChanged(nameof(DeviceMetricsLogText));
        }
    }

    public string TraceRouteLogText
    {
        get => _traceRouteLogText;
        private set
        {
            if (_traceRouteLogText == value) return;
            _traceRouteLogText = value;
            OnChanged(nameof(TraceRouteLogText));
        }
    }

    public string PowerMetricsLogText
    {
        get => _powerMetricsLogText;
        private set
        {
            if (_powerMetricsLogText == value) return;
            _powerMetricsLogText = value;
            OnChanged(nameof(PowerMetricsLogText));
        }
    }

    public string DetectionSensorLogText
    {
        get => _detectionSensorLogText;
        private set
        {
            if (_detectionSensorLogText == value) return;
            _detectionSensorLogText = value;
            OnChanged(nameof(DetectionSensorLogText));
        }
    }

    public bool HasPositionSelection => _selectedPositionEntry is not null;

    public Visibility DeviceMetricsTabIndicatorVisibility => _deviceMetricsTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PositionTabIndicatorVisibility => _positionTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TraceRouteTabIndicatorVisibility => _traceRouteTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PowerMetricsTabIndicatorVisibility => _powerMetricsTabIndicator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetectionSensorTabIndicatorVisibility => _detectionSensorTabIndicator ? Visibility.Visible : Visibility.Collapsed;

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

        _logPollTimer.Interval = TimeSpan.FromSeconds(2);
        _logPollTimer.Tick += (_, __) => PollLogs();

        Loaded += NodesPage_Loaded;
        Unloaded += NodesPage_Unloaded;
    }

    private void NodesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        MeshtasticWin.AppState.Nodes.CollectionChanged -= Nodes_CollectionChanged;
        foreach (var n in MeshtasticWin.AppState.Nodes)
            n.PropertyChanged -= Node_PropertyChanged;

        _logPollTimer.Stop();
    }

    private async void NodesPage_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureMapAsync();
        await PushAllNodesToMapAsync();
        await PushSelectionToMapAsync();
        _logPollTimer.Start();
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

    private async void ExchangeUserInfo_Click(object sender, RoutedEventArgs _)
        => await SendRequestAsync("Exchange User Info", async nodeNum =>
            await RadioClient.Instance.SendNodeInfoRequestAsync(nodeNum));

    private async void ExchangePositions_Click(object sender, RoutedEventArgs _)
        => await SendRequestAsync("Exchange Positions", async nodeNum =>
            await RadioClient.Instance.SendPositionRequestAsync(nodeNum));

    private async void TraceRoute_Click(object sender, RoutedEventArgs _)
        => await SendTraceRouteAsync();

    private void DetailsTabs_SelectionChanged(object sender, SelectionChangedEventArgs _)
    {
        if (DetailsTabs.SelectedIndex < 0) return;

        var logKind = TabIndexToLogKind(DetailsTabs.SelectedIndex);
        if (logKind is null) return;

        ClearTabIndicator(logKind.Value);
        if (Selected is not null)
            ClearPendingIndicator(Selected.IdHex, logKind.Value);

        RefreshSelectedNodeLogs();
    }

    private void PositionLogList_SelectionChanged(object sender, SelectionChangedEventArgs _)
    {
        _selectedPositionEntry = PositionLogList.SelectedItem as PositionLogEntry;
        OnChanged(nameof(HasPositionSelection));

        if (_selectedPositionEntry is not null)
            ShowPositionOnMap(_selectedPositionEntry.Lat, _selectedPositionEntry.Lon);
    }

    private async void OpenMaps_Click(object sender, RoutedEventArgs _)
    {
        if (_selectedPositionEntry is null) return;
        var lat = _selectedPositionEntry.Lat.ToString("0.0000000", CultureInfo.InvariantCulture);
        var lon = _selectedPositionEntry.Lon.ToString("0.0000000", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://www.google.com/maps/search/?api=1&query={lat},{lon}");
        await Launcher.LaunchUriAsync(uri);
    }

    private void ApplyPendingIndicatorsForSelectedNode()
    {
        if (Selected is null)
            return;

        ClearAllTabIndicators();

        if (_pendingLogIndicatorsByNode.TryGetValue(Selected.IdHex, out var pending))
        {
            foreach (var kind in pending)
                SetTabIndicator(kind, true);
        }
    }

    private void RefreshSelectedNodeLogs()
    {
        if (Selected is null)
            return;

        DeviceMetricsLogText = ReadLogText(LogKind.DeviceMetrics);
        TraceRouteLogText = ReadLogText(LogKind.TraceRoute);
        PowerMetricsLogText = ReadLogText(LogKind.PowerMetrics);
        DetectionSensorLogText = ReadLogText(LogKind.DetectionSensor);

        var positionEntries = ReadPositionEntries();
        PositionLogEntries.Clear();
        foreach (var entry in positionEntries)
            PositionLogEntries.Add(entry);

        _selectedPositionEntry = null;
        OnChanged(nameof(HasPositionSelection));
    }

    private void PollLogs()
    {
        foreach (var node in MeshtasticWin.AppState.Nodes)
        {
            if (node.IdHex is null) continue;

            foreach (var kind in AllLogKinds)
            {
                var newWriteTime = GetLogLastWriteTimeUtc(node.IdHex, kind);
                var lastWriteTime = GetLastLogWriteTime(node.IdHex, kind);

                if (newWriteTime == DateTime.MinValue)
                    continue;

                if (newWriteTime > lastWriteTime)
                {
                    SetLastLogWriteTime(node.IdHex, kind, newWriteTime);
                    HandleNewLog(node, kind);
                }
            }
        }
    }

    private void HandleNewLog(NodeLive node, LogKind kind)
    {
        if (Selected is null || !string.Equals(node.IdHex, Selected.IdHex, StringComparison.OrdinalIgnoreCase))
        {
            node.HasLogIndicator = true;
            AddPendingIndicator(node.IdHex, kind);
            return;
        }

        if (!IsTabSelectedForLog(kind))
        {
            SetTabIndicator(kind, true);
            AddPendingIndicator(node.IdHex, kind);
        }

        RefreshSelectedNodeLogs();
    }

    private bool IsTabSelectedForLog(LogKind kind)
        => TabIndexToLogKind(DetailsTabs.SelectedIndex) == kind;

    private void AddPendingIndicator(string nodeId, LogKind kind)
    {
        if (!_pendingLogIndicatorsByNode.TryGetValue(nodeId, out var set))
        {
            set = new HashSet<LogKind>();
            _pendingLogIndicatorsByNode[nodeId] = set;
        }

        set.Add(kind);
    }

    private void ClearPendingIndicator(string nodeId, LogKind kind)
    {
        if (!_pendingLogIndicatorsByNode.TryGetValue(nodeId, out var set))
            return;

        set.Remove(kind);
        if (set.Count == 0)
            _pendingLogIndicatorsByNode.Remove(nodeId);
    }

    private void ClearAllTabIndicators()
    {
        SetTabIndicator(LogKind.DeviceMetrics, false);
        SetTabIndicator(LogKind.Position, false);
        SetTabIndicator(LogKind.TraceRoute, false);
        SetTabIndicator(LogKind.PowerMetrics, false);
        SetTabIndicator(LogKind.DetectionSensor, false);
    }

    private void SetTabIndicator(LogKind kind, bool value)
    {
        switch (kind)
        {
            case LogKind.DeviceMetrics:
                if (_deviceMetricsTabIndicator == value) return;
                _deviceMetricsTabIndicator = value;
                OnChanged(nameof(DeviceMetricsTabIndicatorVisibility));
                break;
            case LogKind.Position:
                if (_positionTabIndicator == value) return;
                _positionTabIndicator = value;
                OnChanged(nameof(PositionTabIndicatorVisibility));
                break;
            case LogKind.TraceRoute:
                if (_traceRouteTabIndicator == value) return;
                _traceRouteTabIndicator = value;
                OnChanged(nameof(TraceRouteTabIndicatorVisibility));
                break;
            case LogKind.PowerMetrics:
                if (_powerMetricsTabIndicator == value) return;
                _powerMetricsTabIndicator = value;
                OnChanged(nameof(PowerMetricsTabIndicatorVisibility));
                break;
            case LogKind.DetectionSensor:
                if (_detectionSensorTabIndicator == value) return;
                _detectionSensorTabIndicator = value;
                OnChanged(nameof(DetectionSensorTabIndicatorVisibility));
                break;
        }
    }

    private void ClearTabIndicator(LogKind kind)
        => SetTabIndicator(kind, false);

    private string ReadLogText(LogKind kind)
    {
        if (Selected is null)
            return "No log entries yet.";

        var lines = NodeLogArchive.ReadTail(ToArchiveType(kind), Selected.IdHex, maxLines: 400);
        return lines.Length == 0 ? "No log entries yet." : string.Join(Environment.NewLine, lines);
    }

    private List<PositionLogEntry> ReadPositionEntries()
    {
        if (Selected is null)
            return new List<PositionLogEntry>();

        return GpsArchive.ReadAll(Selected.IdHex, maxPoints: 5000)
            .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
            .Select(PositionLogEntry.FromPoint)
            .ToList();
    }

    private DateTime GetLastLogWriteTime(string nodeId, LogKind kind)
    {
        if (_lastLogWriteByNode.TryGetValue(nodeId, out var map) && map.TryGetValue(kind, out var ts))
            return ts;
        return DateTime.MinValue;
    }

    private void SetLastLogWriteTime(string nodeId, LogKind kind, DateTime timestampUtc)
    {
        if (!_lastLogWriteByNode.TryGetValue(nodeId, out var map))
        {
            map = new Dictionary<LogKind, DateTime>();
            _lastLogWriteByNode[nodeId] = map;
        }

        map[kind] = timestampUtc;
    }

    private DateTime GetLogLastWriteTimeUtc(string nodeId, LogKind kind)
    {
        var path = GetLogFilePath(nodeId, kind);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private static string GetLogFilePath(string nodeId, LogKind kind)
    {
        var safe = SanitizeNodeId(nodeId);
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshtasticWin", "Logs");

        return kind switch
        {
            LogKind.DeviceMetrics => Path.Combine(baseDir, "device_metrics", $"0x{safe}.log"),
            LogKind.TraceRoute => Path.Combine(baseDir, "traceroute", $"0x{safe}.log"),
            LogKind.PowerMetrics => Path.Combine(baseDir, "power_metrics", $"0x{safe}.log"),
            LogKind.DetectionSensor => Path.Combine(baseDir, "detection_sensor", $"0x{safe}.log"),
            LogKind.Position => Path.Combine(baseDir, "gps", $"0x{safe}.log"),
            _ => Path.Combine(baseDir, "unknown", $"0x{safe}.log")
        };
    }

    private static string SanitizeNodeId(string idHex)
    {
        var safe = (idHex ?? "").Trim();
        if (safe.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            safe = safe[2..];

        safe = new string(safe.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
            safe = "UNKNOWN";

        return safe.ToUpperInvariant();
    }

    private static LogKind? TabIndexToLogKind(int index)
        => index switch
        {
            1 => LogKind.DeviceMetrics,
            2 => LogKind.Position,
            3 => LogKind.TraceRoute,
            4 => LogKind.PowerMetrics,
            5 => LogKind.DetectionSensor,
            _ => null
        };

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

    internal sealed record PositionLogEntry(DateTime TimestampUtc, double Lat, double Lon, double? Alt, string DisplayText)
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

    private enum LogKind
    {
        DeviceMetrics,
        Position,
        TraceRoute,
        PowerMetrics,
        DetectionSensor
    }

    private static readonly LogKind[] AllLogKinds =
    {
        LogKind.DeviceMetrics,
        LogKind.Position,
        LogKind.TraceRoute,
        LogKind.PowerMetrics,
        LogKind.DetectionSensor
    };

    private static NodeLogType ToArchiveType(LogKind kind)
        => kind switch
        {
            LogKind.DeviceMetrics => NodeLogType.DeviceMetrics,
            LogKind.TraceRoute => NodeLogType.TraceRoute,
            LogKind.PowerMetrics => NodeLogType.PowerMetrics,
            LogKind.DetectionSensor => NodeLogType.DetectionSensor,
            _ => NodeLogType.DeviceMetrics
        };

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
