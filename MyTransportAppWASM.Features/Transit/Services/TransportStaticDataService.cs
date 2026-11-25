using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MyTransportAppWASM.Features.Transit.Services;

public sealed class TransportStaticDataService(HttpClient httpClient, IAppCache? cache = null) : ITransportStaticDataService
{
  private static readonly TimeSpan StaticCacheDuration = TimeSpan.FromMinutes(15);
  private readonly ConcurrentDictionary<string, Task<string>> _inFlightMaps = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, Task<List<StaticFrequency>>> _inFlightFrequencies = new(StringComparer.Ordinal);

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
    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
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
        var routeId = Text(row, "route_id");
        if (routeId == null) continue;
        routes.Add(new StaticRoute(routeId, Text(row, "route_short_name"), Text(row, "route_long_name")));
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
      var routeId = Text(properties, "route_id");
      if (routeId == null || routes.ContainsKey(routeId)) continue;
      routes.Add(routeId, new StaticRoute(routeId, Text(properties, "route_short_name"), Text(properties, "route_long_name")));
    }
    return routes.Values.OrderBy(route => route.RouteShortName ?? route.RouteId, StringComparer.OrdinalIgnoreCase).ToList();
  }

  public Task<string> GetMapAsync(TransportStaticData source, string routeId = "", CancellationToken cancellationToken = default) =>
    GetMapAsync(source, routeId, serviceDate: null, cancellationToken);

  public async Task<string> GetMapAsync(TransportStaticData source, string routeId, string? serviceDate, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl)) return "";
    var trimmedRouteId = routeId?.Trim() ?? string.Empty;
    var trimmedDate = serviceDate?.Trim() ?? string.Empty;
    var queryParts = new List<string>(2);
    if (!string.IsNullOrEmpty(trimmedRouteId))
    {
      queryParts.Add($"route_id={Uri.EscapeDataString(trimmedRouteId)}");
    }
    if (!string.IsNullOrEmpty(trimmedDate))
    {
      queryParts.Add($"date={Uri.EscapeDataString(trimmedDate)}");
    }
    var query = queryParts.Count == 0 ? "" : $"?{string.Join('&', queryParts)}";
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
    var trimmedStopId = stopId?.Trim() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(source.BaseUrl) || string.IsNullOrEmpty(trimmedStopId)) return [];
    var trimmedRouteId = routeId?.Trim() ?? string.Empty;
    var routeQuery = string.IsNullOrEmpty(trimmedRouteId) ? "" : $"&route_id={Uri.EscapeDataString(trimmedRouteId)}";
    var url = BuildUrl(source, source.DeparturePath,
      $"?stop_id={Uri.EscapeDataString(trimmedStopId)}{routeQuery}&limit=5");
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
          row.TryGetProperty("route_id", out var rProp) ? rProp.GetString() ?? trimmedRouteId : trimmedRouteId,
          Text(row, "trip_headsign"),
          depTime);
      })
      .ToList();
  }

  public async Task<List<StaticFrequency>> GetFrequenciesAsync(TransportStaticData source, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl) || string.IsNullOrWhiteSpace(source.FrequenciesPath))
    {
      return [];
    }

    var cacheKey = $"static_frequencies:{source.BaseUrl.TrimEnd('/')}|{source.FrequenciesPath.TrimStart('/')}";
    if (cache != null && cache.TryGetValue(cacheKey, out List<StaticFrequency>? cached) && cached != null)
    {
      return cached;
    }

    var sharedTask = _inFlightFrequencies.GetOrAdd(cacheKey, _ => FetchAndCacheFrequenciesAsync(source, cacheKey));
    return await sharedTask.WaitAsync(cancellationToken);
  }

  private async Task<List<StaticFrequency>> FetchAndCacheFrequenciesAsync(TransportStaticData source, string cacheKey)
  {
    try
    {
      const int pageSize = 1000;
      const int maxPages = 100;
      var frequencies = new List<StaticFrequency>();
      for (var offset = 0; offset < pageSize * maxPages; offset += pageSize)
      {
        var url = BuildUrl(source, source.FrequenciesPath,
          $"?include=trip_id,start_time,end_time,headway_secs&sort=trip_id,start_time&limit={pageSize}&offset={offset}");
        using var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var rows = document.RootElement.GetProperty("data");
        foreach (var row in rows.EnumerateArray())
        {
          var tripId = Text(row, "trip_id");
          var startTime = Text(row, "start_time");
          var endTime = Text(row, "end_time");
          var headwaySecs = IntValue(row, "headway_secs");
          if (tripId == null || startTime == null || endTime == null || headwaySecs <= 0) continue;
          frequencies.Add(new StaticFrequency(tripId, startTime, endTime, headwaySecs));
        }
        if (rows.GetArrayLength() < pageSize) break;
      }

      cache?.Set(cacheKey, frequencies, StaticCacheDuration);
      return frequencies;
    }
    finally
    {
      _inFlightFrequencies.TryRemove(cacheKey, out _);
    }
  }

  private static int IntValue(JsonElement row, string property) =>
    row.TryGetProperty(property, out var value)
      ? (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
          ? n
          : (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0))
      : 0;

  private static string BuildRoutesCacheKey(TransportStaticData source) =>
    $"static_routes:{source.BaseUrl.TrimEnd('/')}|{source.RouteInfoPath.TrimStart('/')}|{source.GeoJSONUrlPath.TrimStart('/')}";

  private static string BuildUrl(TransportStaticData source, string path, string query) =>
    $"{source.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}{query}";

  private static string? Text(JsonElement row, string property) =>
    row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
      ? (string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()) : null;
}
