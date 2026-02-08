using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace MeshtasticWin.Pages;

public sealed class TraceRouteLogEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public TraceRouteLogEntry(
        string rawLine,
        DateTime timestampUtc,
        string headerText,
        string pathText,
        string? routeBackHeaderText,
        string? routeBackPathText,
        string overlayHeaderText,
        string overlayRouteText,
        string? overlayRouteBackText,
        string? overlayMetricsText,
        bool isPassive,
        int hopCount,
        IReadOnlyList<RouteMapPoint> routePoints,
        bool canViewRoute)
    {
        RawLine = rawLine;
        TimestampUtc = timestampUtc;
        HeaderText = headerText;
        PathText = pathText;
        RouteBackHeaderText = routeBackHeaderText;
        RouteBackPathText = routeBackPathText;
        OverlayHeaderText = overlayHeaderText;
        OverlayRouteText = overlayRouteText;
        OverlayRouteBackText = overlayRouteBackText;
        OverlayMetricsText = overlayMetricsText;
        IsPassive = isPassive;
        HopCount = hopCount;
        RoutePoints = routePoints;
        CanViewRoute = canViewRoute;
        RouteBackVisibility = string.IsNullOrWhiteSpace(routeBackHeaderText) && string.IsNullOrWhiteSpace(routeBackPathText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        MetricsVisibility = string.IsNullOrWhiteSpace(overlayMetricsText) ? Visibility.Collapsed : Visibility.Visible;
    }

    public string RawLine { get; }
    public DateTime TimestampUtc { get; }
    public bool IsPassive { get; private set; }
    public int HopCount { get; private set; }
    public IReadOnlyList<RouteMapPoint> RoutePoints { get; private set; }
    public bool CanViewRoute { get; private set; }

    public string HeaderText { get; private set; }
    public string PathText { get; private set; }
    public string? RouteBackHeaderText { get; private set; }
    public string? RouteBackPathText { get; private set; }
    public Visibility RouteBackVisibility { get; private set; }

    public string OverlayHeaderText { get; private set; }
    public string OverlayRouteText { get; private set; }
    public string? OverlayRouteBackText { get; private set; }
    public string? OverlayMetricsText { get; private set; }
    public Visibility MetricsVisibility { get; private set; }

    public void UpdateFrom(TraceRouteLogEntry other)
    {
        HeaderText = other.HeaderText;
        PathText = other.PathText;
        RouteBackHeaderText = other.RouteBackHeaderText;
        RouteBackPathText = other.RouteBackPathText;
        OverlayHeaderText = other.OverlayHeaderText;
        OverlayRouteText = other.OverlayRouteText;
        OverlayRouteBackText = other.OverlayRouteBackText;
        OverlayMetricsText = other.OverlayMetricsText;
        IsPassive = other.IsPassive;
        HopCount = other.HopCount;
        RoutePoints = other.RoutePoints;
        CanViewRoute = other.CanViewRoute;
        RouteBackVisibility = string.IsNullOrWhiteSpace(RouteBackHeaderText) && string.IsNullOrWhiteSpace(RouteBackPathText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        MetricsVisibility = string.IsNullOrWhiteSpace(OverlayMetricsText) ? Visibility.Collapsed : Visibility.Visible;

        OnChanged(nameof(HeaderText));
        OnChanged(nameof(PathText));
        OnChanged(nameof(RouteBackHeaderText));
        OnChanged(nameof(RouteBackPathText));
        OnChanged(nameof(RouteBackVisibility));
        OnChanged(nameof(OverlayHeaderText));
        OnChanged(nameof(OverlayRouteText));
        OnChanged(nameof(OverlayRouteBackText));
        OnChanged(nameof(OverlayMetricsText));
        OnChanged(nameof(MetricsVisibility));
        OnChanged(nameof(IsPassive));
        OnChanged(nameof(HopCount));
        OnChanged(nameof(RoutePoints));
        OnChanged(nameof(CanViewRoute));
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record RouteMapPoint(
    [property: System.Text.Json.Serialization.JsonPropertyName("lat")] double Lat,
    [property: System.Text.Json.Serialization.JsonPropertyName("lon")] double Lon,
    [property: System.Text.Json.Serialization.JsonPropertyName("label")] string? Label);
