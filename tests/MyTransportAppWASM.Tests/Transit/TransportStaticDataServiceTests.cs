using System.Net;
using Microsoft.Extensions.Configuration;

namespace MyTransportAppWASM.Tests;

public sealed class TransportStaticDataServiceTests
{
  private static readonly TransportStaticData Source = new(
    "https://worker.example/rapid-bus-kl", "/data/routes", "/map", "/departures");

  [Fact]
  public void ProductionSettingsOmitLocalhostWhileDevelopmentSettingsConfigureLocalWorker()
  {
    var wwwrootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
      "../../../../../MyTransportAppWASM/wwwroot"));
    var prodPath = Path.Combine(wwwrootDir, "appsettings.json");
    var devPath = Path.Combine(wwwrootDir, "appsettings.Development.example.json");

    var prodSettings = new ConfigurationBuilder().AddJsonFile(prodPath).Build();
    var prodProviders = prodSettings.GetSection("TransportProviders").Get<List<TransportProvider>>()!;
    Assert.All(prodProviders, p =>
    {
      var baseUrl = p.StaticData?.BaseUrl ?? string.Empty;
      Assert.DoesNotContain("localhost", baseUrl, StringComparison.OrdinalIgnoreCase);
      if (p.HasStaticData)
      {
        Assert.StartsWith("https://my-gtfs-worker.98heniyschen.workers.dev/", baseUrl, StringComparison.OrdinalIgnoreCase);
      }
    });
    Assert.Equal(6, prodProviders.Count(p => p.HasStaticData));
    Assert.DoesNotContain("localhost", prodSettings["BaziFlow:BaseUrl"] ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    var devSettings = new ConfigurationBuilder()
      .AddJsonFile(prodPath)
      .AddJsonFile(devPath)
      .Build();
    var railProvider = Assert.Single(devSettings.GetSection("TransportProviders").Get<List<TransportProvider>>()!,
      provider => provider.Name == "Rapid Rail KL");

    Assert.False(railProvider.HasRealtimeFeed);
    Assert.True(railProvider.HasStaticData);
    Assert.True(railProvider.HasFrequencyFeed);
    Assert.Equal("Rail", railProvider.TransportType);
    Assert.Equal("http://localhost:8787/rapid-rail-kl", railProvider.StaticData?.BaseUrl);
    Assert.Equal("/departures", railProvider.StaticData?.DeparturePath);
    Assert.Equal("/data/frequencies", railProvider.StaticData?.FrequenciesPath);
    Assert.Equal("http://localhost:8080", devSettings["BaziFlow:BaseUrl"]);
  }

  [Fact]
  public void BindsStaticDataOnAnExistingTransportProvider()
  {
    var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["TransportProviders:0:Name"] = "Rapid Bus KL",
      ["TransportProviders:0:Endpoint"] = "https://vehicle.example/feed",
      ["TransportProviders:0:CenterLat"] = "3.139",
      ["TransportProviders:0:CenterLng"] = "101.6869",
      ["TransportProviders:0:RadiusKm"] = "60",
      ["TransportProviders:0:StaticData:BaseUrl"] = Source.BaseUrl,
      ["TransportProviders:0:StaticData:RouteInfoPath"] = Source.RouteInfoPath,
      ["TransportProviders:0:StaticData:GeoJSONUrlPath"] = Source.GeoJSONUrlPath,
      ["TransportProviders:0:StaticData:DeparturePath"] = Source.DeparturePath
    }).Build();

    var provider = Assert.Single(settings.GetSection("TransportProviders").Get<List<TransportProvider>>()!);
    Assert.Equal(Source, provider.StaticData);
  }

  [Fact]
  public async Task ReadsConfiguredRoutesMapAndEstimatedDepartures()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => """{"data":[{"route_id":"A 1","route_short_name":"A1","route_long_name":"Airport"},{"route_id":"B2","route_long_name":"Downtown"}],"limit":1000,"offset":0}""",
      "/rapid-bus-kl/map" => """{"type":"FeatureCollection","features":[]}""",
      "/rapid-bus-kl/departures" => """{"departures":[{"route_id":"A 1","trip_headsign":"Airport","estimated_departure_at":"2026-09-27T15:30:00+08:00"}]}""",
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var routes = await service.GetRoutesAsync(Source);
    var allMap = await service.GetMapAsync(Source);
    var map = await service.GetMapAsync(Source, "A 1");
    var allDepartures = await service.GetDeparturesAsync(Source, "", "STOP/1");
    var departures = await service.GetDeparturesAsync(Source, "A 1", "STOP/1");

    Assert.Equal([new StaticRoute("A 1", "A1", "Airport"), new StaticRoute("B2", null, "Downtown")], routes);
    Assert.Contains("FeatureCollection", allMap);
    Assert.Contains("FeatureCollection", map);
    Assert.Equal("Airport", Assert.Single(allDepartures).TripHeadsign);
    Assert.Equal("Airport", Assert.Single(departures).TripHeadsign);
    Assert.Equal("https://worker.example/rapid-bus-kl/data/routes?include=route_id,route_short_name,route_long_name&sort=route_id&limit=1000&offset=0", handler.Urls[0]);
    Assert.Equal("https://worker.example/rapid-bus-kl/map", handler.Urls[1]);
    Assert.Equal("https://worker.example/rapid-bus-kl/map?route_id=A%201", handler.Urls[2]);
    Assert.Equal("https://worker.example/rapid-bus-kl/departures?stop_id=STOP%2F1&limit=5", handler.Urls[3]);
    Assert.Equal("https://worker.example/rapid-bus-kl/departures?stop_id=STOP%2F1&route_id=A%201&limit=5", handler.Urls[4]);
  }

  [Fact]
  public async Task UsesPublicMapWhenRouteDataIsBlockedByCors()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => throw new HttpRequestException("Failed to fetch"),
      "/rapid-bus-kl/map" => """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"LineString","coordinates":[]},"properties":{"kind":"route","route_id":"A1","route_short_name":"Airport"}},{"type":"Feature","geometry":{"type":"LineString","coordinates":[]},"properties":{"kind":"route","route_id":"A1","route_short_name":"Airport"}}]}""",
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var routes = await service.GetRoutesAsync(Source);

    Assert.Equal(new StaticRoute("A1", "Airport"), Assert.Single(routes));
    Assert.Equal("https://worker.example/rapid-bus-kl/map", handler.Urls[1]);
  }

  [Fact]
  public async Task UsesPublicMapWhenRouteDataHasMalformedPayload()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => """{"error":"Something went wrong"}""",
      "/rapid-bus-kl/map" => """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"LineString","coordinates":[]},"properties":{"kind":"route","route_id":"A1","route_short_name":"Airport"}}]}""",
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var routes = await service.GetRoutesAsync(Source);

    Assert.Equal(new StaticRoute("A1", "Airport"), Assert.Single(routes));
    Assert.Equal("https://worker.example/rapid-bus-kl/map", handler.Urls[1]);
  }

  [Fact]
  public async Task ReadsDeparturesWithMissingOrMalformedFieldsGracefully()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/departures" => """{"departures":[{"trip_headsign":"Airport","estimated_departure_at":"invalid-date"},{"route_id":"B2"}]}""",
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var departures = await service.GetDeparturesAsync(Source, "A1", "STOP/1");

    Assert.Equal(2, departures.Count);
    Assert.Equal("A1", departures[0].RouteId);
    Assert.Null(departures[0].EstimatedDepartureAt);
    Assert.Equal("B2", departures[1].RouteId);
    Assert.Null(departures[1].TripHeadsign);
    Assert.Null(departures[1].EstimatedDepartureAt);
  }

  [Fact]
  public async Task CoalescesConcurrentMapFetchesAndCachesRoutesAndGeoJson()
  {
    const string mapPayload = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"LineString","coordinates":[]},"properties":{"kind":"route","route_id":"A1","route_short_name":"Airport"}}]}""";
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => throw new HttpRequestException("CORS blocked"),
      "/rapid-bus-kl/map" => mapPayload,
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    using var cache = new AppMemoryCache(sizeLimit: 10);
    var service = new TransportStaticDataService(new HttpClient(handler), cache);

    var routesTask = service.GetRoutesAsync(Source);
    var mapTask = service.GetMapAsync(Source);
    await Task.WhenAll(routesTask, mapTask);

    Assert.Equal(new StaticRoute("A1", "Airport"), Assert.Single(await routesTask));
    Assert.Equal(mapPayload, await mapTask);
    Assert.Equal(1, handler.Urls.Count(u => u.EndsWith("/map", StringComparison.Ordinal)));

    // Subsequent calls hit bounded MemoryCache without issuing network calls
    var routesAgain = await service.GetRoutesAsync(Source);
    var mapAgain = await service.GetMapAsync(Source);
    Assert.Single(routesAgain);
    Assert.Equal(mapPayload, mapAgain);
    Assert.Equal(2, handler.Urls.Count); // only 1 /data/routes attempt + 1 /map fetch total
  }

  [Fact]
  public async Task ReturnsEmptyImmediatelyWhenBaseUrlIsWhitespace()
  {
    var emptySource = new TransportStaticData("", "/data/routes", "/map", "/departures");
    var handler = new FakeHandler(_ => throw new InvalidOperationException("No network calls expected"));
    var service = new TransportStaticDataService(new HttpClient(handler));

    var routes = await service.GetRoutesAsync(emptySource);
    var map = await service.GetMapAsync(emptySource);
    var departures = await service.GetDeparturesAsync(emptySource, "A1", "STOP/1");

    Assert.Empty(routes);
    Assert.Empty(map);
    Assert.Empty(departures);
    Assert.Empty(handler.Urls);
  }

  [Fact]
  public async Task ThrowsServiceUnavailableWithoutRetryingMapEndpoint()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => throw new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable),
      _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetRoutesAsync(Source));
    Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    Assert.Single(handler.Urls);
    Assert.Contains("/rapid-bus-kl/data/routes", handler.Urls[0]);
  }

  [Fact]
  public async Task ThrowsTooManyRequestsWithoutRetryingMapEndpoint()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => throw new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests),
      _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetRoutesAsync(Source));
    Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    Assert.Single(handler.Urls);
    Assert.Contains("/rapid-bus-kl/data/routes", handler.Urls[0]);
  }

  [Fact]
  public async Task TrimsQueryParametersAndSkipsEmptyStopId()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/map" => """{"type":"FeatureCollection","features":[]}""",
      "/rapid-bus-kl/departures" => """{"departures":[]}""",
      _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    await service.GetMapAsync(Source, "   ");
    await service.GetMapAsync(Source, " A 1 ");
    Assert.Empty(await service.GetDeparturesAsync(Source, "A 1", "   "));
    await service.GetDeparturesAsync(Source, "   ", " STOP/1 ");

    Assert.Equal(
    [
      "https://worker.example/rapid-bus-kl/map",
      "https://worker.example/rapid-bus-kl/map?route_id=A%201",
      "https://worker.example/rapid-bus-kl/departures?stop_id=STOP%2F1&limit=5"
    ], handler.Urls);
  }

  [Fact]
  public async Task ReadsAndCachesFrequenciesAndDateScopedMapsAndSkipsWhenPathMissing()
  {
    const string frequenciesPayload = """
      {
        "data": [
          { "trip_id": "AGL_MonFri_0", "start_time": "6:00:00", "end_time": "9:00:00", "headway_secs": 180 },
          { "trip_id": "AGL_MonFri_0", "start_time": "9:00:00", "end_time": "17:00:00", "headway_secs": 300 }
        ],
        "limit": 1000,
        "offset": 0
      }
      """;

    var railSource = new TransportStaticData(
      "https://worker.example/rapid-rail-kl", "/data/routes", "/map", "/departures", "/data/frequencies");
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-rail-kl/data/frequencies" => frequenciesPayload,
      "/rapid-rail-kl/map" => """{"type":"FeatureCollection","features":[]}""",
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    using var cache = new AppMemoryCache(sizeLimit: 10);
    var service = new TransportStaticDataService(new HttpClient(handler), cache);

    Assert.Empty(await service.GetFrequenciesAsync(Source)); // FrequenciesPath is empty on bus source

    var first = await service.GetFrequenciesAsync(railSource);
    var second = await service.GetFrequenciesAsync(railSource);
    var datedMap = await service.GetMapAsync(railSource, "", "2026-11-25");
    var routeDatedMap = await service.GetMapAsync(railSource, "AG", "2026-11-24");

    Assert.Same(first, second);
    Assert.Equal(2, first.Count);
    Assert.Equal(new StaticFrequency("AGL_MonFri_0", "6:00:00", "9:00:00", 180), first[0]);
    Assert.Contains("FeatureCollection", datedMap);
    Assert.Contains("FeatureCollection", routeDatedMap);
    Assert.Equal(
    [
      "https://worker.example/rapid-rail-kl/data/frequencies?include=trip_id,start_time,end_time,headway_secs&sort=trip_id,start_time&limit=1000&offset=0",
      "https://worker.example/rapid-rail-kl/map?date=2026-11-25",
      "https://worker.example/rapid-rail-kl/map?route_id=AG&date=2026-11-24"
    ], handler.Urls);
  }

  private sealed class FakeHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
  {
    public List<string> Urls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      Urls.Add(request.RequestUri!.AbsoluteUri);
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
    }
  }
}
