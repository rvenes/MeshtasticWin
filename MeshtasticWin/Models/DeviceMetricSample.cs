using System;
using System.Globalization;

namespace MeshtasticWin.Models;

public sealed record DeviceMetricSample(
    DateTime Timestamp,
    double? BatteryVolts,
    double? ChannelUtilization,
    double? Airtime,
    bool? IsPowered)
{
    public double? BatteryPercent { get; init; }

    public DateTime TimestampLocal => Timestamp.Kind == DateTimeKind.Utc
        ? Timestamp.ToLocalTime()
        : Timestamp.ToLocalTime();

    public string TimestampText => TimestampLocal.ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string BatteryDisplay
    {
        get
        {
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
