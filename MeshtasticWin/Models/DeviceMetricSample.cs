using System;
using System.Globalization;

namespace MeshtasticWin.Models;

public sealed record DeviceMetricSample
{
    public DeviceMetricSample(DateTime timestamp, double? batteryVolts, double? channelUtilization, double? airtime, bool? isPowered)
    {
        Timestamp = timestamp;
        BatteryVolts = batteryVolts;
        ChannelUtilization = channelUtilization;
        Airtime = airtime;
        IsPowered = isPowered;
    }

    public DateTime Timestamp { get; set; }

    public double? BatteryVolts { get; set; }

    public double? ChannelUtilization { get; set; }

    public double? Airtime { get; set; }

    public bool? IsPowered { get; set; }

    public double? BatteryPercent { get; set; }

    public DateTime TimestampLocal => Timestamp.Kind == DateTimeKind.Utc
        ? Timestamp.ToLocalTime()
        : Timestamp.ToLocalTime();

    public string TimestampText => TimestampLocal.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string BatteryDisplay
    {
        get
        {
            if (BatteryVolts.HasValue && BatteryPercent.HasValue)
            {
                return $"BAT {BatteryVolts.Value.ToString("0.##", CultureInfo.InvariantCulture)}V ({BatteryPercent.Value.ToString("0.#", CultureInfo.InvariantCulture)}%)";
            }

            if (BatteryVolts.HasValue)
                return $"BAT {BatteryVolts.Value.ToString("0.##", CultureInfo.InvariantCulture)}V";

            if (BatteryPercent.HasValue)
                return $"BAT {BatteryPercent.Value.ToString("0.#", CultureInfo.InvariantCulture)}%";
            return "BAT —";
        }
    }

    public string ChannelUtilizationDisplay =>
        ChannelUtilization.HasValue
            ? $"CH {ChannelUtilization.Value.ToString("0.#", CultureInfo.InvariantCulture)}%"
            : "CH —";

    public string AirtimeDisplay =>
        Airtime.HasValue
            ? $"AIR {Airtime.Value.ToString("0.#", CultureInfo.InvariantCulture)}%"
            : "AIR —";

    public string PoweredDisplay => IsPowered == true ? "PWRD" : "";
}
