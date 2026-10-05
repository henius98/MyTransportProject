using System.Globalization;
using System.Text.Json;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services;

public sealed class TransportStaticDataService(HttpClient httpClient) : ITransportStaticDataService
{
  public async Task<List<StaticRoute>> GetRoutesAsync(TransportStaticData source, CancellationToken cancellationToken = default)
  {
    try
    {
      return await GetRoutesFromDataAsync(source, cancellationToken);
    }
    catch (HttpRequestException)
    {
      return await GetRoutesFromMapAsync(source, cancellationToken);
    }
  }

  private async Task<List<StaticRoute>> GetRoutesFromDataAsync(TransportStaticData source, CancellationToken cancellationToken)
  {
    const int pageSize = 1000;
    var routes = new List<StaticRoute>();
    for (var offset = 0; ; offset += pageSize)
    {
      var url = BuildUrl(source, source.RouteInfoPath,
        $"?include=route_id,route_short_name,route_long_name&sort=route_id&limit={pageSize}&offset={offset}");
      using var response = await httpClient.GetAsync(url, cancellationToken);
      response.EnsureSuccessStatusCode();
      using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
      var rows = document.RootElement.GetProperty("data");
      foreach (var row in rows.EnumerateArray())
      {
        var id = row.GetProperty("route_id").GetString();
        if (string.IsNullOrWhiteSpace(id)) continue;
        var name = Text(row, "route_short_name") ?? Text(row, "route_long_name") ?? id;
        routes.Add(new StaticRoute(id, name));
      }
      if (rows.GetArrayLength() < pageSize) break;
    }
    return routes;
  }

  private async Task<List<StaticRoute>> GetRoutesFromMapAsync(TransportStaticData source, CancellationToken cancellationToken)
  {
    using var document = JsonDocument.Parse(await GetMapAsync(source, "", cancellationToken));
    var routes = new Dictionary<string, StaticRoute>(StringComparer.Ordinal);
    foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
    {
      var properties = feature.GetProperty("properties");
      if (Text(properties, "kind") != "route") continue;
      var id = Text(properties, "route_id");
      if (id == null || routes.ContainsKey(id)) continue;
      var name = Text(properties, "route_short_name") ?? Text(properties, "route_long_name") ?? id;
      routes.Add(id, new StaticRoute(id, name));
    }
    return routes.Values.OrderBy(route => route.Name, StringComparer.OrdinalIgnoreCase).ToList();
  }

  public async Task<string> GetMapAsync(TransportStaticData source, string routeId, CancellationToken cancellationToken = default)
  {
    var query = string.IsNullOrEmpty(routeId) ? "" : $"?route_id={Uri.EscapeDataString(routeId)}";
    var url = BuildUrl(source, source.GeoJSONUrlPath, query);
    return await httpClient.GetStringAsync(url, cancellationToken);
  }

  public async Task<List<StaticDeparture>> GetDeparturesAsync(TransportStaticData source, string routeId, string stopId, CancellationToken cancellationToken = default)
  {
    var url = BuildUrl(source, source.DeparturePath,
      $"?stop_id={Uri.EscapeDataString(stopId)}&route_id={Uri.EscapeDataString(routeId)}&limit=5");
    using var response = await httpClient.GetAsync(url, cancellationToken);
    response.EnsureSuccessStatusCode();
    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    return document.RootElement.GetProperty("departures").EnumerateArray()
      .Select(row => new StaticDeparture(
        row.GetProperty("route_id").GetString() ?? routeId,
        Text(row, "trip_headsign"),
        DateTimeOffset.Parse(row.GetProperty("estimated_departure_at").GetString()!, CultureInfo.InvariantCulture)))
      .ToList();
  }

  private static string BuildUrl(TransportStaticData source, string path, string query) =>
    $"{source.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}{query}";

  private static string? Text(JsonElement row, string property) =>
    row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
      ? (string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()) : null;
}
