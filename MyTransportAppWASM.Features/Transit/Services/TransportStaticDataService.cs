using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MyTransportAppWASM.Features.Transit.Services;

public sealed class TransportStaticDataService(HttpClient httpClient) : ITransportStaticDataService
{
  private readonly ConcurrentDictionary<string, Lazy<Task<List<StaticRoute>>>> _inFlightRoutes = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlightMaps = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, Lazy<Task<List<StaticFrequency>>>> _inFlightFrequencies = new(StringComparer.Ordinal);

  public Task<List<StaticRoute>> GetRoutesAsync(TransportStaticData source, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl)) return Task.FromResult<List<StaticRoute>>([]);

    var inFlightKey = BuildRoutesInFlightKey(source);
    return CoalesceAsync(_inFlightRoutes, inFlightKey, () => FetchRoutesAsync(source), cancellationToken);
  }

  private async Task<List<StaticRoute>> FetchRoutesAsync(TransportStaticData source)
  {
    try
    {
      return await GetRoutesFromDataAsync(source, CancellationToken.None);
    }
    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
    {
      throw;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return await GetRoutesFromMapAsync(source, CancellationToken.None);
    }
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
    var geoJson = await GetMapAsync(source, cancellationToken: cancellationToken);
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

  public Task<string> GetMapAsync(TransportStaticData source, string routeId = "", string? serviceDate = null, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl)) return Task.FromResult("");
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
    return CoalesceAsync(_inFlightMaps, url, () => httpClient.GetStringAsync(url), cancellationToken);
  }

  public Task<List<StaticFrequency>> GetFrequenciesAsync(TransportStaticData source, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(source.BaseUrl) || string.IsNullOrWhiteSpace(source.FrequenciesPath))
    {
      return Task.FromResult<List<StaticFrequency>>([]);
    }

    var inFlightKey = $"static_frequencies:{source.BaseUrl.TrimEnd('/')}|{source.FrequenciesPath.TrimStart('/')}";
    return CoalesceAsync(_inFlightFrequencies, inFlightKey, () => FetchFrequenciesAsync(source), cancellationToken);
  }

  private async Task<List<StaticFrequency>> FetchFrequenciesAsync(TransportStaticData source)
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

    return frequencies;
  }

  private static async Task<T> CoalesceAsync<T>(
    ConcurrentDictionary<string, Lazy<Task<T>>> inFlightMap,
    string key,
    Func<Task<T>> fetch,
    CancellationToken cancellationToken)
  {
    var inFlight = inFlightMap.GetOrAdd(key, _ =>
    {
      Lazy<Task<T>>? entry = null;
      entry = new Lazy<Task<T>>(async () =>
      {
        try
        {
          return await fetch();
        }
        finally
        {
          inFlightMap.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, entry!));
        }
      });
      return entry;
    });
    return await inFlight.Value.WaitAsync(cancellationToken);
  }

  private static int IntValue(JsonElement row, string property) =>
    row.TryGetProperty(property, out var value)
      ? (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
          ? n
          : (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0))
      : 0;

  private static string BuildRoutesInFlightKey(TransportStaticData source) =>
    $"static_routes:{source.BaseUrl.TrimEnd('/')}|{source.RouteInfoPath.TrimStart('/')}|{source.GeoJSONUrlPath.TrimStart('/')}";

  private static string BuildUrl(TransportStaticData source, string path, string query) =>
    $"{source.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}{query}";

  private static string? Text(JsonElement row, string property) =>
    row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
      ? (string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()) : null;
}
