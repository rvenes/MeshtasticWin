using System;
using System.Globalization;

namespace MeshtasticWin.Models;

public sealed record EnvironmentMetricSample
{
    public EnvironmentMetricSample(DateTime timestampUtc, double? temperatureC, double? relativeHumidity, double? barometricPressure)
    {
        TimestampUtc = timestampUtc;
        TemperatureC = temperatureC;
        RelativeHumidity = relativeHumidity;
        BarometricPressure = barometricPressure;
    }

    public DateTime TimestampUtc { get; set; }

    public double? TemperatureC { get; set; }

    public double? RelativeHumidity { get; set; }

    public double? BarometricPressure { get; set; }

    public DateTime TimestampLocal => TimestampUtc.Kind == DateTimeKind.Utc
        ? TimestampUtc.ToLocalTime()
        : TimestampUtc.ToLocalTime();

    public string TimestampText => TimestampLocal.ToString("yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture);

    public string TemperatureDisplay => TemperatureC.HasValue
        ? $"Temp {TemperatureC.Value.ToString("0.##", CultureInfo.InvariantCulture)} C"
        : "Temp -";

    public string HumidityDisplay => RelativeHumidity.HasValue
        ? $"Humidity {RelativeHumidity.Value.ToString("0.##", CultureInfo.InvariantCulture)} %"
        : "Humidity -";

    public string PressureDisplay => BarometricPressure.HasValue
        ? $"Pressure {BarometricPressure.Value.ToString("0.###", CultureInfo.InvariantCulture)} hPa"
        : "Pressure -";
}
