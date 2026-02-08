using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace MeshtasticWin.Models;

public sealed class NodeLive : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string IdHex { get; }

    // Used for sorting/filtering.
    public DateTime LastHeardUtc { get; private set; } = DateTime.MinValue;

    // Optional (can be shown in UI).
    public ulong NodeNum { get; set; }

    public string ShortId
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IdHex))
                return "";

            var s = IdHex.Trim();

            if (s.StartsWith("fr=", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(3).Trim();

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);

            if (s.Length >= 4)
                return s[^4..].ToUpperInvariant();

            return s.ToUpperInvariant();
        }
    }

    private string _longName = "";
    public string LongName
    {
        get => _longName;
        set { if (_longName != value) { _longName = value; OnChanged(nameof(LongName)); OnChanged(nameof(Name)); } }
    }

    private string _shortName = "";
    public string ShortName
    {
        get => _shortName;
        set { if (_shortName != value) { _shortName = value; OnChanged(nameof(ShortName)); OnChanged(nameof(Name)); } }
    }

    private string _sub = "";
    public string Sub
    {
        get => _sub;
        set { if (_sub != value) { _sub = value; OnChanged(nameof(Sub)); } }
    }

    private string _snr = "—";
    public string SNR
    {
        get => _snr;
        set { if (_snr != value) { _snr = value; OnChanged(nameof(SNR)); } }
    }

    private string _rssi = "—";
    public string RSSI
    {
        get => _rssi;
        set { if (_rssi != value) { _rssi = value; OnChanged(nameof(RSSI)); } }
    }

    private string _lastHeard = "—";
    public string LastHeard
    {
        get => _lastHeard;
        set { if (_lastHeard != value) { _lastHeard = value; OnChanged(nameof(LastHeard)); } }
    }

    // GPS fields.
    private double _lat;
    public double Latitude { get => _lat; set { if (_lat != value) { _lat = value; OnChanged(nameof(Latitude)); OnChanged(nameof(HasPosition)); } } }

    private double _lon;
    public double Longitude { get => _lon; set { if (_lon != value) { _lon = value; OnChanged(nameof(Longitude)); OnChanged(nameof(HasPosition)); } } }

    private DateTime _lastPosUtc = DateTime.MinValue;
    public DateTime LastPositionUtc { get => _lastPosUtc; set { if (_lastPosUtc != value) { _lastPosUtc = value; OnChanged(nameof(LastPositionUtc)); OnChanged(nameof(LastPositionText)); } } }

    public bool HasPosition => LastPositionUtc != DateTime.MinValue;

    public string LastPositionText
    {
        get
        {
            if (!HasPosition) return "—";
            var local = LastPositionUtc.ToLocalTime();
            return local.ToString("HH:mm:ss");
        }
    }

    public bool UpdatePosition(double lat, double lon, DateTime tsUtc, double? alt = null)
    {
        Latitude = lat;
        Longitude = lon;
        LastPositionUtc = tsUtc;
        return true;
    }

    public bool HasUnread => MeshtasticWin.AppState.HasUnread(IdHex);

    public Visibility UnreadVisible => HasUnread ? Visibility.Visible : Visibility.Collapsed;

    private bool _hasLogIndicator;
    public bool HasLogIndicator
    {
        get => _hasLogIndicator;
        set
        {
            if (_hasLogIndicator == value) return;
            _hasLogIndicator = value;
            OnChanged(nameof(HasLogIndicator));
            OnChanged(nameof(LogIndicatorVisible));
        }
    }

    public Visibility LogIndicatorVisible => HasLogIndicator ? Visibility.Visible : Visibility.Collapsed;

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnChanged(nameof(IsVisible));
            OnChanged(nameof(NodeVisibility));
        }
    }

    public Visibility NodeVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

    // Prefer name from NodeInfo when available, otherwise fall back to ShortId/IdHex.
    public string Name
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LongName))
                return LongName;
            if (!string.IsNullOrWhiteSpace(ShortName))
                return ShortName;
            if (!string.IsNullOrWhiteSpace(ShortId))
                return ShortId;
            return IdHex;
        }
    }

    public NodeLive(string idHex)
    {
        IdHex = idHex;
        Sub = "Seen on mesh";

        // Update unread indicator when AppState changes.
        MeshtasticWin.AppState.UnreadChanged += peer =>
        {
            if (string.IsNullOrWhiteSpace(peer))
                return;

            if (string.Equals(peer, IdHex, StringComparison.OrdinalIgnoreCase))
            {
                OnChanged(nameof(HasUnread));
                OnChanged(nameof(UnreadVisible));
            }
        };

        Touch();
    }

    public void Touch()
    {
        LastHeardUtc = DateTime.UtcNow;
        LastHeard = DateTime.Now.ToString("HH:mm:ss");
        OnChanged(nameof(LastHeardUtc));
    }

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
