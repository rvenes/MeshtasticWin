using System.Text.Json.Serialization;

namespace MeshtasticWin.Models;

public sealed record GeoPoint(
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon);
