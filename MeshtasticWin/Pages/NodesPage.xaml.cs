using AppDataPaths = MeshtasticWin.Services.AppDataPaths;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace MeshtasticWin.Pages;

public sealed partial class NodesPage : Page, INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

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
    private System.Threading.Tasks.Task? _mapInitializationTask;
    private bool _mapEventsAttached;
    private bool _mapConfigured;
    private bool _mapResourceLoggingAttached;
    private string? _mapFolderPath;
    private Uri? _mapUri;
    private readonly HashSet<string> _enabledTrackNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Lat, double Lon)> _lastTrackPointByNode = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<LogKind, DateTime>> _lastLogWriteByNode = new();
    private readonly Dictionary<string, Dictionary<LogKind, DateTime>> _lastViewedByNode = new();
    private readonly Dictionary<string, Dictionary<LogKind, DateTime>> _lastAppendedByNode = new();
    private readonly Dictionary<string, HashSet<LogKind>> _pendingLogIndicatorsByNode = new();

    private bool _deviceMetricsTabIndicator;
    private bool _positionTabIndicator;
    private bool _traceRouteTabIndicator;
    private bool _powerMetricsTabIndicator;
    private bool _detectionSensorTabIndicator;

    private string _traceRouteLogText = "No log entries yet.";
    private string _powerMetricsLogText = "No log entries yet.";
    private string _detectionSensorLogText = "No log entries yet.";

    internal ObservableCollection<PositionLogEntry> PositionLogEntries { get; } = new();
    private PositionLogEntry? _selectedPositionEntry;
    private int _positionLogRetentionDays = 7;
    private readonly ObservableCollection<DeviceMetricSample> _deviceMetricSamples = new();
    public ObservableCollection<DeviceMetricSample> DeviceMetricSamples => _deviceMetricSamples;

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
            OnChanged(nameof(SelectedShortNameText));
            OnChanged(nameof(HasSelectedPosition));
            OnChanged(nameof(IsTraceRouteEnabled));
            OnChanged(nameof(TraceRouteButtonText));
            OnChanged(nameof(GpsTrackButtonText));

            if (_selected is not null)
            {
                _selected.HasLogIndicator = false;
                ApplyPendingIndicatorsForSelectedNode();
                RefreshSelectedNodeLogs();
                var logKind = TabIndexToLogKind(DetailsTabs.SelectedIndex);
                if (logKind is not null)
                    MarkTabViewed(logKind.Value, _selected.IdHex);
            }

            _ = PushSelectionToMapAsync();
        }
    }

    public bool HasSelection => Selected is not null;
    public bool HasSelectedPosition => Selected?.HasPosition ?? false;

    public string SelectedTitle => Selected?.Name ?? "Select a node";
    public string SelectedIdHex => Selected?.IdHex ?? "—";
    public string SelectedNodeNumText => Selected is null || Selected.NodeNum == 0 ? "—" : Selected.NodeNum.ToString(CultureInfo.InvariantCulture);
    public string SelectedLastHeardText
    {
        get
        {
            if (Selected is null || Selected.LastHeardUtc == DateTime.MinValue)
                return "—";

            var local = Selected.LastHeardUtc.ToLocalTime();
            var relative = FormatRelativeAge(Selected.LastHeardUtc);
            return $"{local:HH:mm:ss} ({relative})";
        }
    }
    public string SelectedPosText
    {
        get
        {
            if (Selected is null || !Selected.HasPosition)
                return "—";

            var lat = Selected.Latitude.ToString("0.000000", CultureInfo.InvariantCulture);
            var lon = Selected.Longitude.ToString("0.000000", CultureInfo.InvariantCulture);
            var relative = FormatRelativeAge(Selected.LastPositionUtc);
            var local = Selected.LastPositionUtc.ToLocalTime();
            return $"{local:HH:mm:ss} {lat}, {lon} ({relative})";
        }
    }
    public string SelectedShortNameText
    {
        get
        {
            if (Selected is null)
                return "—";

            return string.IsNullOrWhiteSpace(Selected.ShortName) ? "—" : Selected.ShortName;
        }
    }

    public double PositionLogRetentionDaysValue
    {
        get => _positionLogRetentionDays;
        set
        {
            var next = (int)Math.Max(1, Math.Round(value));
            if (_positionLogRetentionDays == next) return;
            _positionLogRetentionDays = next;
            OnChanged(nameof(PositionLogRetentionDaysValue));
        }
    }

    public bool IsTraceRouteEnabled => HasSelection && !_traceRouteCooldownActive;

    public string GpsTrackButtonText => IsSelectedTrackEnabled ? "Hide GPS track" : "Load GPS track";

    public string TraceRouteButtonText =>
        _traceRouteCooldownActive
            ? $"Trace Route ({_traceRouteRemainingSeconds}s)"
            : "Trace Route";

    public string DeviceMetricsCountText => $"Readings Total: {_deviceMetricSamples.Count}";

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

        _deviceMetricSamples.CollectionChanged += DeviceMetricSamples_CollectionChanged;

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

        DeviceMetricsLogService.SampleAdded -= DeviceMetricsLogService_SampleAdded;
        _logPollTimer.Stop();
    }

    private async void NodesPage_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureMapAsync();
        await PushAllNodesToMapAsync();
        await PushSelectionToMapAsync();
        SeedLogWriteTimes();
        DeviceMetricsLogService.SampleAdded += DeviceMetricsLogService_SampleAdded;
        _logPollTimer.Start();
    }

    private async System.Threading.Tasks.Task EnsureMapAsync()
    {
        if (_mapConfigured)
            return;

        if (_mapInitializationTask is null)
            _mapInitializationTask = InitializeMapAsync();

        await _mapInitializationTask;
    }

    private async System.Threading.Tasks.Task InitializeMapAsync()
    {
        if (_mapConfigured)
            return;

        try
        {
            await MapView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            RadioClient.Instance.AddLogFromUiThread($"Map initialization failed: {ex.Message}");
            ShowMapFallback("Map initialization failed.");
            _mapInitializationTask = null;
            return;
        }

        var wv = MapView.CoreWebView2;
        if (wv is null)
        {
            ShowMapFallback("Map initialization failed.");
            _mapInitializationTask = null;
            return;
        }

        if (!_mapEventsAttached)
        {
            wv.WebMessageReceived += CoreWebView2_WebMessageReceived;
            wv.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _mapEventsAttached = true;
        }

        if (!_mapResourceLoggingAttached)
        {
            try
            {
                wv.AddWebResourceRequestedFilter("https://appassets.local/*", CoreWebView2WebResourceContext.All);
                wv.WebResourceRequested += CoreWebView2_WebResourceRequested;
                _mapResourceLoggingAttached = true;
            }
            catch (Exception ex)
            {
                RadioClient.Instance.AddLogFromUiThread($"Map resource logging setup failed: {ex.Message}");
            }
        }

        var installPath = ResolveInstallPath();
        _mapFolderPath = Path.GetFullPath(Path.Combine(installPath, "Assets"));
        RadioClient.Instance.AddLogFromUiThread($"Map assets path: {_mapFolderPath}");

        if (!Directory.Exists(_mapFolderPath))
        {
            RadioClient.Instance.AddLogFromUiThread($"Map assets missing: {_mapFolderPath}");
            ShowMapFallback("Map assets missing.");
            _mapInitializationTask = null;
            return;
        }

        var mapHtml = Path.Combine(_mapFolderPath, "Map", "map.html");
        if (!File.Exists(mapHtml))
        {
            RadioClient.Instance.AddLogFromUiThread($"Map HTML missing: {mapHtml}");
            ShowMapFallback("Map HTML missing.");
            _mapInitializationTask = null;
            return;
        }

        _mapUri = new Uri("https://appassets.local/Map/map.html");
        RadioClient.Instance.AddLogFromUiThread($"Map navigation URI: {_mapUri}");

        try
        {
            _mapReady = false;
            wv.SetVirtualHostNameToFolderMapping("appassets.local", _mapFolderPath, CoreWebView2HostResourceAccessKind.Allow);
            _ = InjectConsoleBridgeAsync(wv);
            MapView.Source = _mapUri;

            if (AppState.EnableWebViewDevTools)
            {
                try
                {
                    wv.OpenDevToolsWindow();
                    RadioClient.Instance.AddLogFromUiThread("Map DevTools opened.");
                }
                catch (Exception ex)
                {
                    RadioClient.Instance.AddLogFromUiThread($"Map DevTools failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            RadioClient.Instance.AddLogFromUiThread($"Map navigation setup failed: {ex.Message}");
            ShowMapFallback("Map navigation setup failed.");
            _mapInitializationTask = null;
            return;
        }

        HideMapFallback();
        _mapConfigured = true;
    }

    private static string ResolveInstallPath()
    {
        if (Packaging.IsPackaged())
        {
            try
            {
                return Package.Current.InstalledLocation.Path;
            }
            catch
            {
                return TrimBaseDirectory();
            }
        }

        return TrimBaseDirectory();
    }

    private static string TrimBaseDirectory()
    {
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(type)) return;

            if (type == "console")
            {
                var level = root.TryGetProperty("level", out var levelEl) ? levelEl.GetString() : "log";
                var message = root.TryGetProperty("message", out var messageEl) ? messageEl.GetString() : "";
                RadioClient.Instance.AddLogFromUiThread($"Map console [{level}]: {message}");
                return;
            }

            if (type == "ready")
            {
                _mapReady = true;
                RadioClient.Instance.AddLogFromUiThread("Map ready message received.");
                _ = PushAllNodesToMapAsync();
                _ = PushSelectionToMapAsync();
                _ = PushEnabledTracksToMapAsync();
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
                    points = points.Select(p => new GeoPointWithTimestamp(
                        p.Lat,
                        p.Lon,
                        p.TsUtc.ToString("o", CultureInfo.InvariantCulture)))
                };
                MapView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
            }
        }
        catch { }
    }

    private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = e.Request?.Uri ?? "";
            if (uri.StartsWith("https://appassets.local/", StringComparison.OrdinalIgnoreCase))
            {
                RadioClient.Instance.AddLogFromUiThread($"Map resource requested: {uri}");
            }
        }
        catch
        {
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var uri = MapView.Source?.ToString() ?? MapView.CoreWebView2?.Source ?? "unknown";
        if (e.IsSuccess)
        {
            RadioClient.Instance.AddLogFromUiThread($"Map navigation completed: {uri}");
            HideMapFallback();
            _ = MapView.CoreWebView2?.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
            return;
        }

        _mapReady = false;
        RadioClient.Instance.AddLogFromUiThread($"Map navigation failed: {e.WebErrorStatus} ({uri})");
        ShowMapFallback($"Map failed to load: {e.WebErrorStatus}");
        _mapConfigured = false;
        _mapInitializationTask = null;
    }

    private static async System.Threading.Tasks.Task InjectConsoleBridgeAsync(CoreWebView2 webView)
    {
        const string script = """
            (() => {
                if (window.__meshtasticConsoleBridgeInstalled) {
                    return;
                }
                window.__meshtasticConsoleBridgeInstalled = true;
                const levels = ["log", "info", "warn", "error", "debug"];
                levels.forEach(level => {
                    const original = console[level];
                    console[level] = (...args) => {
                        try {
                            const message = args.map(arg => {
                                if (typeof arg === "string") return arg;
                                try { return JSON.stringify(arg); } catch { return String(arg); }
                            }).join(" ");
                            chrome.webview.postMessage({ type: "console", level, message });
                        } catch { }
                        if (typeof original === "function") {
                            original.apply(console, args);
                        }
                    };
                });
            })();
            """;

        try
        {
            await webView.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch
        {
        }
    }

    private void ShowMapFallback(string message)
    {
        MapFallbackText.Text = message;
        MapFallbackText.Visibility = Visibility.Visible;
    }

    private void HideMapFallback()
    {
        MapFallbackText.Visibility = Visibility.Collapsed;
    }

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (NodeLive n in e.NewItems)
            {
                n.PropertyChanged += Node_PropertyChanged;
                SeedLogWriteTimesForNode(n);
            }

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
            OnChanged(nameof(SelectedShortNameText));
            OnChanged(nameof(SelectedNodeNumText));
            OnChanged(nameof(HasSelectedPosition));
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

        if (sender is NodeLive updatedNode
            && (e.PropertyName is nameof(NodeLive.Latitude) or nameof(NodeLive.Longitude)))
        {
            TryAppendTrackPoint(updatedNode);
        }
    }

    private static bool IsOnlineByRssi(NodeLive n)
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

        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "nodes", nodes }, s_jsonOptions));
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task PushSelectionToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "selected", idHex = Selected?.IdHex }, s_jsonOptions));
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
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "zoomTo", idHex = Selected.IdHex }, s_jsonOptions));
    }

    private void ToggleGpsTrack_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;

        var nodeId = NormalizeNodeId(Selected.IdHex);
        if (_enabledTrackNodeIds.Remove(nodeId))
        {
            _lastTrackPointByNode.Remove(nodeId);
            SendTrackClear(nodeId);
            OnChanged(nameof(GpsTrackButtonText));
            return;
        }

        _enabledTrackNodeIds.Add(nodeId);
        var points = GpsArchive.ReadAll(Selected.IdHex, maxPoints: 5000)
            .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.TsUtc)
            .Select(p => new GeoPoint(p.Lat, p.Lon))
            .ToArray();

        SendTrackSet(nodeId, points);
        if (points.Length > 0)
            _lastTrackPointByNode[nodeId] = (points[^1].Lat, points[^1].Lon);
        OnChanged(nameof(GpsTrackButtonText));
    }

    private void ResetMap_Click(object sender, RoutedEventArgs e)
    {
        _enabledTrackNodeIds.Clear();
        _lastTrackPointByNode.Clear();
        SendTrackClearAll();
        OnChanged(nameof(GpsTrackButtonText));
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

    private async void DetailsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailsTabs.SelectedIndex < 0) return;

        if (DetailsTabs.SelectedIndex == 0)
        {
            await HandleMapTabSelectedAsync();
        }

        var logKind = TabIndexToLogKind(DetailsTabs.SelectedIndex);
        if (logKind is null) return;

        if (Selected is not null)
            MarkTabViewed(logKind.Value, Selected.IdHex);

        RefreshSelectedNodeLogs();

    }

    private async System.Threading.Tasks.Task HandleMapTabSelectedAsync()
    {
        await EnsureMapAsync();
        _ = MapView.CoreWebView2?.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
        LogMapSizing();
    }

    private void LogMapSizing()
    {
        var mapWidth = MapView.ActualWidth;
        var mapHeight = MapView.ActualHeight;
        var parent = MapView.Parent as FrameworkElement;
        var parentWidth = parent?.ActualWidth ?? 0;
        var parentHeight = parent?.ActualHeight ?? 0;

        RadioClient.Instance.AddLogFromUiThread(
            $"Map WebView2 size: {mapWidth:0.##}x{mapHeight:0.##} (parent: {parentWidth:0.##}x{parentHeight:0.##})");

        if (mapWidth <= 1 || mapHeight <= 1 || parentWidth <= 1 || parentHeight <= 1)
        {
            RadioClient.Instance.AddLogFromUiThread(
                $"Map WebView2 size warning: map={mapWidth:0.##}x{mapHeight:0.##}, parent={parentWidth:0.##}x{parentHeight:0.##}");
        }
    }

    private void PositionLogList_SelectionChanged(object sender, SelectionChangedEventArgs _)
    {
        _selectedPositionEntry = PositionLogList.SelectedItem as PositionLogEntry;
        OnChanged(nameof(HasPositionSelection));
    }

    private void PositionLogList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PositionLogEntry entry)
            return;

        PositionLogList.SelectedItem = entry;
        DetailsTabs.SelectedIndex = 0;
        ShowPositionOnMap(entry.Lat, entry.Lon);
    }

    private void PositionLogEntry_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowPositionLogContextFlyout(sender);
    }

    private void ShowPositionLogContextFlyout(object sender)
    {
        if (sender is not FrameworkElement element)
            return;

        var entry = element.DataContext as PositionLogEntry;
        if (entry is not null)
            PositionLogList.SelectedItem = entry;

        var flyout = element.ContextFlyout;
        flyout?.ShowAt(element);
    }

    private void DeviceMetricSamples_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnChanged(nameof(DeviceMetricsCountText));
        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);
    }

    private void DeviceMetricsLogService_SampleAdded(string nodeId, DeviceMetricSample sample)
    {
        if (Selected is null || !string.Equals(NormalizeNodeId(Selected.IdHex), nodeId, StringComparison.OrdinalIgnoreCase))
            return;

        var viewer = FindScrollViewer(DeviceMetricsLogList);
        var wasAtTop = viewer is null || viewer.VerticalOffset <= 0.5;
        var priorOffset = viewer?.VerticalOffset ?? 0;

        if (_deviceMetricSamples.Count > 0 && _deviceMetricSamples[0].Timestamp == sample.Timestamp)
            return;

        _deviceMetricSamples.Insert(0, sample);
        if (_deviceMetricSamples.Count > 2000)
            _deviceMetricSamples.RemoveAt(_deviceMetricSamples.Count - 1);

        if (viewer is not null)
        {
            var targetOffset = wasAtTop ? 0 : priorOffset;
            _ = DispatcherQueue.TryEnqueue(() => viewer.ChangeView(null, targetOffset, null, true));
        }
    }

    private void RefreshDeviceMetricsSamples()
    {
        if (Selected is null)
            return;

        var viewer = FindScrollViewer(DeviceMetricsLogList);
        var wasAtTop = viewer is null || viewer.VerticalOffset <= 0.5;
        var priorOffset = viewer?.VerticalOffset ?? 0;

        var samples = DeviceMetricsLogService.GetSamples(Selected.IdHex, maxSamples: 2000);
        _deviceMetricSamples.Clear();
        foreach (var sample in samples)
            _deviceMetricSamples.Add(sample);

        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);

        if (viewer is not null)
        {
            var targetOffset = wasAtTop ? 0 : priorOffset;
            _ = DispatcherQueue.TryEnqueue(() => viewer.ChangeView(null, targetOffset, null, true));
        }
    }

    private void RefreshPositionEntries(List<PositionLogEntry>? entries = null)
    {
        var viewer = FindScrollViewer(PositionLogList);
        var wasAtTop = viewer is null || viewer.VerticalOffset <= 0.5;
        var priorOffset = viewer?.VerticalOffset ?? 0;

        var positionEntries = entries ?? ReadPositionEntries();
        PositionLogEntries.Clear();
        foreach (var entry in positionEntries)
            PositionLogEntries.Add(entry);

        if (viewer is not null)
        {
            var targetOffset = wasAtTop ? 0 : priorOffset;
            _ = DispatcherQueue.TryEnqueue(() => viewer.ChangeView(null, targetOffset, null, true));
        }
    }

    private async void PositionLogOpenMaps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is PositionLogEntry entry)
            await OpenMapsForPositionAsync(entry.Lat, entry.Lon);
    }

    private async void OpenCurrentPosition_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null || !Selected.HasPosition)
            return;

        await OpenMapsForPositionAsync(Selected.Latitude, Selected.Longitude);
    }

    private async System.Threading.Tasks.Task OpenMapsForPositionAsync(double latValue, double lonValue)
    {
        var lat = latValue.ToString("0.0000000", CultureInfo.InvariantCulture);
        var lon = lonValue.ToString("0.0000000", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://www.google.com/maps?q={lat},{lon}");
        await Launcher.LaunchUriAsync(uri);
    }

    private async void DeletePositionLogOlder_Click(object sender, RoutedEventArgs e)
        => await DeletePositionLogOlderAsync();

    private async void ExportPositionLog_Click(object _, RoutedEventArgs _1)
    {
        if (Selected is null)
            return;

        var entries = ReadPositionEntries();
        if (entries.Count == 0)
        {
            await ShowStatusAsync("No position log entries to export.");
            return;
        }

        var suggestedName = $"{Selected.ShortId}_position_log";
        var exportPath = await PickExportPathAsync(suggestedName);
        if (string.IsNullOrWhiteSpace(exportPath))
            return;

        var extension = Path.GetExtension(exportPath).ToLowerInvariant();
        var content = extension switch
        {
            ".gpx" => BuildPositionLogGpx(entries, Selected),
            ".csv" => BuildPositionLogCsv(entries, Selected),
            ".txt" => BuildPositionLogTxt(entries),
            _ => BuildPositionLogCsv(entries, Selected)
        };

        try
        {
            var targetDir = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
                Directory.CreateDirectory(targetDir);
            File.WriteAllText(exportPath, content, Encoding.UTF8);
            await ShowStatusAsync($"Position log exported to:{Environment.NewLine}{exportPath}");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Export failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task<string?> PickExportPathAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        picker.FileTypeChoices.Add("GPX", new List<string> { ".gpx" });
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });

        if (App.MainWindowInstance is null)
            return null;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static string BuildPositionLogTxt(IEnumerable<PositionLogEntry> entries)
        => string.Join(Environment.NewLine, entries.Select(entry => entry.DisplayText));

    private static string BuildPositionLogCsv(IEnumerable<PositionLogEntry> entries, NodeLive node)
    {
        var sb = new StringBuilder();
        sb.AppendLine("timestamp,lat,lon,alt,nodeId,nodeName");

        foreach (var entry in entries.OrderBy(entry => entry.TimestampUtc))
        {
            var timestamp = entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture);
            var lat = entry.Lat.ToString("0.0000000", CultureInfo.InvariantCulture);
            var lon = entry.Lon.ToString("0.0000000", CultureInfo.InvariantCulture);
            var alt = entry.Alt.HasValue ? entry.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
            var nodeId = EscapeCsv(node.IdHex ?? "");
            var nodeName = EscapeCsv(node.Name ?? "");

            sb.AppendLine($"{timestamp},{lat},{lon},{alt},{nodeId},{nodeName}");
        }

        return sb.ToString();
    }

    private static string BuildPositionLogGpx(IEnumerable<PositionLogEntry> entries, NodeLive node)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("gpx", "http://www.topografix.com/GPX/1/1");
            writer.WriteAttributeString("version", "1.1");
            writer.WriteAttributeString("creator", "MeshtasticWin");

            writer.WriteStartElement("trk");
            writer.WriteElementString("name", node.Name ?? "MeshtasticWin");
            writer.WriteStartElement("trkseg");

            foreach (var entry in entries.OrderBy(entry => entry.TimestampUtc))
            {
                writer.WriteStartElement("trkpt");
                writer.WriteAttributeString("lat", entry.Lat.ToString("0.0000000", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("lon", entry.Lon.ToString("0.0000000", CultureInfo.InvariantCulture));
                if (entry.Alt.HasValue)
                    writer.WriteElementString("ele", entry.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture));
                writer.WriteElementString("time", entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private void ClearDeviceMetrics_Click(object _, RoutedEventArgs _1)
    {
        if (Selected is null) return;
        DeviceMetricsLogService.ClearSamples(Selected.IdHex);
        _deviceMetricSamples.Clear();
        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);
    }

    private async void ShowLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (!TryGetLogKind(sender, out var kind))
            return;

        var folderPath = GetLogFolderPath(kind);
        try
        {
            Directory.CreateDirectory(folderPath);
            var opened = await Launcher.LaunchFolderPathAsync(folderPath);
            if (!opened)
                await ShowStatusAsync("Unable to open the log folder.");
        }
        catch (Exception ex)
        {
            await ShowStatusAsync($"Unable to open the log folder: {ex.Message}");
        }
    }

    private async void DeleteLogOlder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (!TryGetLogKind(sender, out var kind))
            return;

        await DeleteLogOlderAsync(kind);
    }

    private async void DeleteLogAll_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;

        if (!TryGetLogKind(sender, out var kind))
            return;

        await DeleteLogAllAsync(kind);
    }

    private async void SaveDeviceMetrics_Click(object _, RoutedEventArgs _1)
    {
        if (Selected is null)
            return;

        var logPath = DeviceMetricsLogService.GetLogPath(Selected.IdHex);
        if (!File.Exists(logPath))
        {
            await ShowStatusAsync("No device metrics log found for this node yet.");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"{Selected.ShortId}_device_metrics"
        };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });

        if (App.MainWindowInstance is null)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var content = await FileIO.ReadTextAsync(await StorageFile.GetFileFromPathAsync(logPath));
        await FileIO.WriteTextAsync(file, content);
    }

    private void ApplyPendingIndicatorsForSelectedNode()
    {
        if (Selected is null)
            return;

        ClearAllTabIndicators();

        if (_pendingLogIndicatorsByNode.TryGetValue(NormalizeNodeId(Selected.IdHex), out var pending))
        {
            foreach (var kind in pending)
                SetTabIndicator(kind, true);
        }
    }

    private void RefreshSelectedNodeLogs()
    {
        if (Selected is null)
            return;

        RefreshDeviceMetricsSamples();
        TraceRouteLogText = ReadLogText(LogKind.TraceRoute);
        PowerMetricsLogText = ReadLogText(LogKind.PowerMetrics);
        DetectionSensorLogText = ReadLogText(LogKind.DetectionSensor);

        RefreshPositionEntries();

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
                    HandleNewLog(node, kind, newWriteTime);
                }
            }
        }
    }

    private void HandleNewLog(NodeLive node, LogKind kind, DateTime appendedUtc)
    {
        var nodeId = NormalizeNodeId(node.IdHex);
        SetLastAppendedTimestamp(nodeId, kind, appendedUtc);

        var isSelectedNode = Selected is not null
            && string.Equals(NormalizeNodeId(Selected.IdHex), nodeId, StringComparison.OrdinalIgnoreCase);
        var isSelectedTab = IsTabSelectedForLog(kind);

        if (isSelectedNode && isSelectedTab)
        {
            SetLastViewedTimestamp(nodeId, kind, appendedUtc);
            ClearTabIndicator(kind);
            ClearPendingIndicator(nodeId, kind);
            RefreshSelectedNodeLogs();
            return;
        }

        var shouldShowIndicator = appendedUtc > GetLastViewedTimestamp(nodeId, kind);
        if (shouldShowIndicator)
        {
            if (isSelectedNode)
                SetTabIndicator(kind, true);
            AddPendingIndicator(nodeId, kind);
        }

        if (isSelectedNode)
            RefreshSelectedNodeLogs();
    }

    private bool IsTabSelectedForLog(LogKind kind)
        => TabIndexToLogKind(DetailsTabs.SelectedIndex) == kind;

    private void AddPendingIndicator(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_pendingLogIndicatorsByNode.TryGetValue(normalized, out var set))
        {
            set = new HashSet<LogKind>();
            _pendingLogIndicatorsByNode[normalized] = set;
        }

        set.Add(kind);
        UpdateNodeLogIndicator(normalized);
    }

    private void ClearPendingIndicator(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_pendingLogIndicatorsByNode.TryGetValue(normalized, out var set))
            return;

        set.Remove(kind);
        if (set.Count == 0)
            _pendingLogIndicatorsByNode.Remove(normalized);

        UpdateNodeLogIndicator(normalized);
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
        if (lines.Length == 0)
            return "No log entries yet.";

        if (kind == LogKind.TraceRoute)
            return string.Join(Environment.NewLine, lines.Select(FormatTraceRouteLine));

        return string.Join(Environment.NewLine, lines);
    }

    private List<PositionLogEntry> ReadPositionEntries()
    {
        if (Selected is null)
            return new List<PositionLogEntry>();

        return GpsArchive.ReadAll(Selected.IdHex, maxPoints: 5000)
            .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.TsUtc)
            .Select(PositionLogEntry.FromPoint)
            .ToList();
    }

    private DateTime GetLastLogWriteTime(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (_lastLogWriteByNode.TryGetValue(normalized, out var map) && map.TryGetValue(kind, out var ts))
            return ts;
        return DateTime.MinValue;
    }

    private void SetLastLogWriteTime(string nodeId, LogKind kind, DateTime timestampUtc)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_lastLogWriteByNode.TryGetValue(normalized, out var map))
        {
            map = new Dictionary<LogKind, DateTime>();
            _lastLogWriteByNode[normalized] = map;
        }

        map[kind] = timestampUtc;
    }

    private DateTime GetLastViewedTimestamp(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (_lastViewedByNode.TryGetValue(normalized, out var map) && map.TryGetValue(kind, out var ts))
            return ts;
        return DateTime.MinValue;
    }

    private void SetLastViewedTimestamp(string nodeId, LogKind kind, DateTime timestampUtc)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_lastViewedByNode.TryGetValue(normalized, out var map))
        {
            map = new Dictionary<LogKind, DateTime>();
            _lastViewedByNode[normalized] = map;
        }

        map[kind] = timestampUtc;
    }

    private DateTime GetLastAppendedTimestamp(string nodeId, LogKind kind)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (_lastAppendedByNode.TryGetValue(normalized, out var map) && map.TryGetValue(kind, out var ts))
            return ts;
        return DateTime.MinValue;
    }

    private void SetLastAppendedTimestamp(string nodeId, LogKind kind, DateTime timestampUtc)
    {
        var normalized = NormalizeNodeId(nodeId);
        if (!_lastAppendedByNode.TryGetValue(normalized, out var map))
        {
            map = new Dictionary<LogKind, DateTime>();
            _lastAppendedByNode[normalized] = map;
        }

        map[kind] = timestampUtc;
    }

    private static DateTime GetLogLastWriteTimeUtc(string nodeId, LogKind kind)
    {
        var path = GetLogFilePath(nodeId, kind);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static string GetLogFilePath(string nodeId, LogKind kind)
    {
        var safe = SanitizeNodeId(nodeId);
        var baseDir = AppDataPaths.LogsPath;

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

    private static string NormalizeNodeId(string idHex)
        => $"0x{SanitizeNodeId(idHex)}";

    private void SeedLogWriteTimes()
    {
        foreach (var node in MeshtasticWin.AppState.Nodes)
            SeedLogWriteTimesForNode(node);
    }

    private void SeedLogWriteTimesForNode(NodeLive node)
    {
        if (node.IdHex is null)
            return;

        foreach (var kind in AllLogKinds)
        {
            var writeTime = GetLogLastWriteTimeUtc(node.IdHex, kind);
            if (writeTime != DateTime.MinValue)
                SetLastLogWriteTime(node.IdHex, kind, writeTime);
        }
    }

    private void MarkTabViewed(LogKind kind, string nodeId)
    {
        var appended = GetLastAppendedTimestamp(nodeId, kind);
        var now = DateTime.UtcNow;
        SetLastViewedTimestamp(nodeId, kind, appended > now ? appended : now);
        ClearTabIndicator(kind);
        ClearPendingIndicator(nodeId, kind);
    }

    private void UpdateNodeLogIndicator(string nodeId)
    {
        var normalized = NormalizeNodeId(nodeId);
        var hasPending = _pendingLogIndicatorsByNode.TryGetValue(normalized, out var set) && set.Count > 0;
        var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n =>
            string.Equals(NormalizeNodeId(n.IdHex), normalized, StringComparison.OrdinalIgnoreCase));

        if (node is null)
            return;

        var isSelected = Selected is not null
            && string.Equals(NormalizeNodeId(Selected.IdHex), normalized, StringComparison.OrdinalIgnoreCase);

        node.HasLogIndicator = hasPending && !isSelected;
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
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private bool IsSelectedTrackEnabled
        => Selected is not null && _enabledTrackNodeIds.Contains(NormalizeNodeId(Selected.IdHex));

    private async System.Threading.Tasks.Task PushEnabledTracksToMapAsync()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;

        foreach (var nodeId in _enabledTrackNodeIds.ToList())
        {
            var points = GpsArchive.ReadAll(nodeId, maxPoints: 5000)
                .Where(p => !string.Equals(p.Src, "nodeinfo_bootstrap", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.TsUtc)
                .Select(p => new GeoPoint(p.Lat, p.Lon))
                .ToArray();

            SendTrackSet(nodeId, points);
            if (points.Length > 0)
                _lastTrackPointByNode[nodeId] = (points[^1].Lat, points[^1].Lon);
        }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void TryAppendTrackPoint(NodeLive node)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        if (!node.HasPosition) return;
        var nodeId = NormalizeNodeId(node.IdHex);
        if (!_enabledTrackNodeIds.Contains(nodeId)) return;

        var point = (Lat: node.Latitude, Lon: node.Longitude);
        if (_lastTrackPointByNode.TryGetValue(nodeId, out var lastPoint))
        {
            if (Math.Abs(lastPoint.Lat - point.Lat) < 0.0000001 && Math.Abs(lastPoint.Lon - point.Lon) < 0.0000001)
                return;
        }

        _lastTrackPointByNode[nodeId] = point;
        SendTrackAppend(nodeId, point.Lat, point.Lon);
    }

    private void SendTrackSet(string nodeId, IReadOnlyList<GeoPoint> points)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "trackSet", idHex = nodeId, points };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private void SendTrackAppend(string nodeId, double lat, double lon)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "trackAppend", idHex = nodeId, point = new GeoPoint(lat, lon) };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private void SendTrackClear(string nodeId)
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        var payload = new { type = "trackClear", idHex = nodeId };
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, s_jsonOptions));
    }

    private void SendTrackClearAll()
    {
        if (!_mapReady || MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"trackClearAll\"}");
    }

    private static string FormatRelativeAge(DateTime utcTimestamp)
    {
        if (utcTimestamp == DateTime.MinValue)
            return "—";

        var delta = DateTime.UtcNow - utcTimestamp;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 60)
        {
            var minutes = Math.Max(1, (int)Math.Floor(delta.TotalMinutes));
            return $"{minutes} min ago";
        }

        if (delta.TotalHours < 24)
        {
            var hours = (int)Math.Floor(delta.TotalHours);
            return $"{hours} hour{(hours == 1 ? "" : "s")} ago";
        }

        var days = (int)Math.Floor(delta.TotalDays);
        return $"{days} day{(days == 1 ? "" : "s")} ago";
    }

    private sealed record PositionLogKey(DateTime TimestampUtc, double Lat, double Lon);

    private sealed record GeoPointWithTimestamp(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lon")] double Lon,
        [property: JsonPropertyName("tsUtc")] string TsUtc);

    internal sealed record PositionLogEntry(DateTime TimestampUtc, double Lat, double Lon, double? Alt, string Src, string DisplayText)
    {
        public bool HasValidPosition =>
            !double.IsNaN(Lat) &&
            !double.IsNaN(Lon) &&
            !double.IsInfinity(Lat) &&
            !double.IsInfinity(Lon) &&
            Lat is >= -90 and <= 90 &&
            Lon is >= -180 and <= 180;

        public static PositionLogEntry FromPoint(GpsArchive.PositionPoint point)
        {
            var altText = point.Alt.HasValue
                ? $" alt={point.Alt.Value.ToString("0.##", CultureInfo.InvariantCulture)}"
                : "";
            var lat = point.Lat.ToString("0.0000000", CultureInfo.InvariantCulture);
            var lon = point.Lon.ToString("0.0000000", CultureInfo.InvariantCulture);
            var tsLocal = point.TsUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);
            var display = $"{tsLocal} | {lat}, {lon}{altText}";
            return new PositionLogEntry(point.TsUtc, point.Lat, point.Lon, point.Alt, point.Src, display);
        }
    }

    private static string FormatTraceRouteLine(string rawLine)
    {
        var parts = rawLine.Split(new[] { " | " }, 2, StringSplitOptions.None);
        var tsPart = parts.Length > 0 ? parts[0] : "";
        var summary = parts.Length > 1 ? parts[1] : "";

        var timestamp = TryParseTimestamp(tsPart);
        var tsText = timestamp?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture) ?? tsPart;

        var parsed = ParseTraceRouteSummary(summary);
        var hopPath = parsed.Route.Count > 0 ? parsed.Route : parsed.RouteBack;
        var hopNames = hopPath.Count > 0 ? hopPath.Select(ResolveNodeName) : Array.Empty<string>();
        var hopCount = hopPath.Count;

        var toSnr = parsed.SnrTowards.Count > 0
            ? FormatSnrValue(parsed.SnrTowards[^1])
            : parsed.RxSnr.HasValue ? FormatSnrValue(parsed.RxSnr.Value) : "—";
        var backSnr = parsed.SnrBack.Count > 0
            ? FormatSnrValue(parsed.SnrBack[^1])
            : parsed.RxSnr.HasValue ? FormatSnrValue(parsed.RxSnr.Value) : "—";

        var snrTowardsText = parsed.SnrTowards.Count > 0
            ? $" | snrTowards=[{string.Join(", ", parsed.SnrTowards.Select(FormatSnrValue))}]"
            : "";
        var snrBackText = parsed.SnrBack.Count > 0
            ? $" | snrBack=[{string.Join(", ", parsed.SnrBack.Select(FormatSnrValue))}]"
            : "";
        var rxMetricsText = "";
        if (parsed.RxSnr.HasValue)
            rxMetricsText += $" | rxSNR={FormatSnrValue(parsed.RxSnr.Value)}";
        if (parsed.RxRssi.HasValue)
            rxMetricsText += $" | rxRSSI={parsed.RxRssi.Value.ToString("0.0", CultureInfo.InvariantCulture)}";

        var pathText = hopNames.Any() ? string.Join(" -> ", hopNames) : "—";

        return $"{tsText} | hops={hopCount} | toSNR={toSnr} backSNR={backSnr}{snrTowardsText}{snrBackText}{rxMetricsText} | path: {pathText}";
    }

    private static DateTimeOffset? TryParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return null;
    }

    private static TraceRouteParsed ParseTraceRouteSummary(string summary)
    {
        var route = new List<uint>();
        var snrTowards = new List<int>();
        var routeBack = new List<uint>();
        var snrBack = new List<int>();
        double? rxSnr = null;
        double? rxRssi = null;

        var tokens = (summary ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentLabel = "";

        foreach (var token in tokens)
        {
            if (token.EndsWith(':'))
            {
                currentLabel = token;
                continue;
            }

            switch (currentLabel)
            {
                case "route:":
                    if (uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var routeNum))
                        route.Add(routeNum);
                    break;
                case "snr_towards:":
                    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snrTo))
                        snrTowards.Add(snrTo);
                    break;
                case "route_back:":
                    if (uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var routeBackNum))
                        routeBack.Add(routeBackNum);
                    break;
                case "snr_back:":
                    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snrBk))
                        snrBack.Add(snrBk);
                    break;
                case "rx_snr:":
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var rxSnrValue))
                        rxSnr = rxSnrValue;
                    break;
                case "rx_rssi:":
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var rxRssiValue))
                        rxRssi = rxRssiValue;
                    break;
            }
        }

        return new TraceRouteParsed(route, snrTowards, routeBack, snrBack, rxSnr, rxRssi);
    }

    private static string ResolveNodeName(uint nodeNum)
    {
        var node = MeshtasticWin.AppState.Nodes.FirstOrDefault(n => n.NodeNum == nodeNum);
        if (node is not null)
            return node.Name;

        return $"0x{nodeNum:x8}";
    }

    private static string FormatSnrValue(int value)
        => FormatSnrValue((double)value);

    private static string FormatSnrValue(double value)
        => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static bool TryGetLogKind(object sender, out LogKind kind)
    {
        if (sender is FrameworkElement element && element.Tag is string tag &&
            Enum.TryParse(tag, out LogKind parsed))
        {
            kind = parsed;
            return true;
        }

        kind = default;
        return false;
    }

    private static string GetLogFolderPath(LogKind kind)
    {
        var baseDir = AppDataPaths.LogsPath;
        return kind switch
        {
            LogKind.DeviceMetrics => Path.Combine(baseDir, "device_metrics"),
            LogKind.TraceRoute => Path.Combine(baseDir, "traceroute"),
            LogKind.PowerMetrics => Path.Combine(baseDir, "power_metrics"),
            LogKind.DetectionSensor => Path.Combine(baseDir, "detection_sensor"),
            LogKind.Position => AppDataPaths.GpsPath,
            _ => baseDir
        };
    }

    private async System.Threading.Tasks.Task DeleteLogOlderAsync(LogKind kind)
    {
        switch (kind)
        {
            case LogKind.Position:
                await DeletePositionLogOlderAsync();
                break;
            case LogKind.DeviceMetrics:
                await DeleteDeviceMetricsOlderAsync();
                break;
            case LogKind.TraceRoute:
            case LogKind.PowerMetrics:
            case LogKind.DetectionSensor:
                await DeleteTextLogOlderAsync(kind);
                break;
        }
    }

    private async System.Threading.Tasks.Task DeleteLogAllAsync(LogKind kind)
    {
        switch (kind)
        {
            case LogKind.Position:
                await DeletePositionLogAllAsync();
                break;
            case LogKind.DeviceMetrics:
                DeleteDeviceMetricsAll();
                break;
            case LogKind.TraceRoute:
            case LogKind.PowerMetrics:
            case LogKind.DetectionSensor:
                DeleteTextLogAll(kind);
                break;
        }
    }

    private async System.Threading.Tasks.Task DeletePositionLogOlderAsync()
    {
        if (Selected is null)
            return;

        var entries = ReadPositionEntries();
        if (entries.Count == 0)
        {
            await ShowStatusAsync("No position log entries to delete.");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_positionLogRetentionDays);
        var remaining = entries
            .Where(entry => entry.TimestampUtc >= cutoff)
            .OrderByDescending(entry => entry.TimestampUtc)
            .ToList();

        var selectedKey = _selectedPositionEntry is null
            ? null
            : new PositionLogKey(_selectedPositionEntry.TimestampUtc, _selectedPositionEntry.Lat, _selectedPositionEntry.Lon);

        var points = remaining
            .OrderBy(entry => entry.TimestampUtc)
            .Select(entry => new GpsArchive.PositionPoint(entry.Lat, entry.Lon, entry.TimestampUtc, entry.Alt, entry.Src))
            .ToList();

        GpsArchive.WriteAll(Selected.IdHex, points);

        RefreshPositionEntries(remaining);

        if (selectedKey is not null)
        {
            var restored = PositionLogEntries.FirstOrDefault(entry =>
                entry.TimestampUtc == selectedKey.TimestampUtc &&
                Math.Abs(entry.Lat - selectedKey.Lat) < 0.0000001 &&
                Math.Abs(entry.Lon - selectedKey.Lon) < 0.0000001);
            PositionLogList.SelectedItem = restored;
        }
    }

    private async System.Threading.Tasks.Task DeletePositionLogAllAsync()
    {
        if (Selected is null)
            return;

        var path = GetLogFilePath(Selected.IdHex, LogKind.Position);
        if (!File.Exists(path))
        {
            await ShowStatusAsync("No position log entries to delete.");
            return;
        }

        File.Delete(path);
        PositionLogEntries.Clear();
        _selectedPositionEntry = null;
        OnChanged(nameof(HasPositionSelection));
    }

    private async System.Threading.Tasks.Task DeleteDeviceMetricsOlderAsync()
    {
        if (Selected is null)
            return;

        var path = DeviceMetricsLogService.GetLogPath(Selected.IdHex);
        if (!File.Exists(path))
        {
            await ShowStatusAsync("No device metrics log entries to delete.");
            return;
        }

        var lines = File.ReadAllLines(path);
        var cutoff = DateTime.UtcNow.AddDays(-_positionLogRetentionDays);
        var filtered = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("timestamp_utc", StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(line);
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length == 0)
                continue;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                filtered.Add(line);
                continue;
            }

            if (timestamp.ToUniversalTime() >= cutoff)
                filtered.Add(line);
        }

        DeviceMetricsLogService.ClearSamples(Selected.IdHex);

        var hasDataLines = filtered.Any(line => !line.StartsWith("timestamp_utc", StringComparison.OrdinalIgnoreCase));
        if (hasDataLines)
        {
            if (!filtered.Any(line => line.StartsWith("timestamp_utc", StringComparison.OrdinalIgnoreCase)))
                filtered.Insert(0, "timestamp_utc,battery_volts,battery_percent,channel_utilization,airtime,is_powered");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, filtered);
        }

        RefreshDeviceMetricsSamples();
    }

    private void DeleteDeviceMetricsAll()
    {
        if (Selected is null)
            return;

        DeviceMetricsLogService.ClearSamples(Selected.IdHex);
        _deviceMetricSamples.Clear();
        DeviceMetricsGraph.SetSamples(_deviceMetricSamples);
    }

    private async System.Threading.Tasks.Task DeleteTextLogOlderAsync(LogKind kind)
    {
        if (Selected is null)
            return;

        var path = GetLogFilePath(Selected.IdHex, kind);
        if (!File.Exists(path))
        {
            await ShowStatusAsync("No log entries to delete.");
            return;
        }

        var lines = File.ReadAllLines(path);
        var cutoff = DateTime.UtcNow.AddDays(-_positionLogRetentionDays);
        var filtered = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(new[] { " | " }, 2, StringSplitOptions.None);
            if (parts.Length == 0)
                continue;

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                filtered.Add(line);
                continue;
            }

            if (timestamp.ToUniversalTime() >= cutoff)
                filtered.Add(line);
        }

        if (filtered.Count == 0)
        {
            File.Delete(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, filtered);
        }

        RefreshSelectedNodeLogs();
    }

    private void DeleteTextLogAll(LogKind kind)
    {
        if (Selected is null)
            return;

        var path = GetLogFilePath(Selected.IdHex, kind);
        if (File.Exists(path))
            File.Delete(path);

        RefreshSelectedNodeLogs();
    }

    private readonly record struct TraceRouteParsed(
        List<uint> Route,
        List<int> SnrTowards,
        List<uint> RouteBack,
        List<int> SnrBack,
        double? RxSnr,
        double? RxRssi);

    private enum LogKind
    {
        DeviceMetrics,
        Position,
        TraceRoute,
        PowerMetrics,
        DetectionSensor
    }

    private static readonly LogKind[] AllLogKinds =
    [
        LogKind.DeviceMetrics,
        LogKind.Position,
        LogKind.TraceRoute,
        LogKind.PowerMetrics,
        LogKind.DetectionSensor
    ];

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
