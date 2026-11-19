using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MyTransportAppWASM.Features.Transit.Services;

public sealed class TransportStaticDataService(HttpClient httpClient, IAppCache? cache = null) : ITransportStaticDataService
{
  private static readonly TimeSpan StaticCacheDuration = TimeSpan.FromMinutes(15);
  private readonly ConcurrentDictionary<string, Task<string>> _inFlightMaps = new(StringComparer.Ordinal);

  public async Task<List<StaticRoute>> GetRoutesAsync(TransportStaticData source, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl)) return [];

    var cacheKey = BuildRoutesCacheKey(source);
    if (cache != null && cache.TryGetValue(cacheKey, out List<StaticRoute>? cached) && cached != null)
    {
      return cached;
    }

    List<StaticRoute> routes;
    try
    {
      routes = await GetRoutesFromDataAsync(source, cancellationToken);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
    {
      throw;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      routes = await GetRoutesFromMapAsync(source, cancellationToken);
    }

    cache?.Set(cacheKey, routes, StaticCacheDuration);

    return routes;
  }

  private async Task<List<StaticRoute>> GetRoutesFromDataAsync(TransportStaticData source, CancellationToken cancellationToken)
  {
    const int pageSize = 1000;
    const int maxPages = 100;
    var routes = new List<StaticRoute>();
    for (var offset = 0; offset < pageSize * maxPages; offset += pageSize)
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
    var geoJson = await GetMapAsync(source, "", cancellationToken);
    if (string.IsNullOrEmpty(geoJson)) return [];
    using var document = JsonDocument.Parse(geoJson);
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

  public async Task<string> GetMapAsync(TransportStaticData source, string routeId = "", CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl)) return "";
    var query = string.IsNullOrEmpty(routeId) ? "" : $"?route_id={Uri.EscapeDataString(routeId)}";
    var url = BuildUrl(source, source.GeoJSONUrlPath, query);
    var cacheKey = $"static_map:{url}";

    if (cache != null && cache.TryGetValue(cacheKey, out string? cached) && cached != null)
    {
      return cached;
    }

    var sharedTask = _inFlightMaps.GetOrAdd(url, key => FetchAndCacheMapAsync(key, cacheKey));
    return await sharedTask.WaitAsync(cancellationToken);
  }

  private async Task<string> FetchAndCacheMapAsync(string url, string cacheKey)
  {
    try
    {
      var geoJson = await httpClient.GetStringAsync(url);
      cache?.Set(cacheKey, geoJson, StaticCacheDuration);
      return geoJson;
    }
    finally
    {
      _inFlightMaps.TryRemove(url, out _);
    }
  }

  public async Task<List<StaticDeparture>> GetDeparturesAsync(TransportStaticData source, string routeId, string stopId, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl)) return [];
    var routeQuery = string.IsNullOrEmpty(routeId) ? "" : $"&route_id={Uri.EscapeDataString(routeId)}";
    var url = BuildUrl(source, source.DeparturePath,
      $"?stop_id={Uri.EscapeDataString(stopId)}{routeQuery}&limit=5");
    using var response = await httpClient.GetAsync(url, cancellationToken);
    response.EnsureSuccessStatusCode();
    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    return document.RootElement.GetProperty("departures").EnumerateArray()
      .Select(row =>
      {
        var depStr = row.TryGetProperty("estimated_departure_at", out var depProp) ? depProp.GetString() : null;
        DateTimeOffset? depTime = DateTimeOffset.TryParse(depStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
          ? parsed
          : null;
        return new StaticDeparture(
          row.TryGetProperty("route_id", out var rProp) ? rProp.GetString() ?? routeId : routeId,
          Text(row, "trip_headsign"),
          depTime);
      })
      .ToList();
  }

  private static string BuildRoutesCacheKey(TransportStaticData source) =>
    $"static_routes:{source.BaseUrl.TrimEnd('/')}|{source.RouteInfoPath.TrimStart('/')}|{source.GeoJSONUrlPath.TrimStart('/')}";

  private static string BuildUrl(TransportStaticData source, string path, string query) =>
    $"{source.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}{query}";

  private static string? Text(JsonElement row, string property) =>
    row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
      ? (string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()) : null;
}
