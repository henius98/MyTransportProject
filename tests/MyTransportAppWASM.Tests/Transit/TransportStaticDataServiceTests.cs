using System.Net;
using Microsoft.Extensions.Configuration;

namespace MyTransportAppWASM.Tests;

public sealed class TransportStaticDataServiceTests
{
  private static readonly TransportStaticData Source = new(
    "https://worker.example/rapid-bus-kl", "/data/routes", "/map");

  [Fact]
  public void ProductionSettingsOmitLocalhostWhileDevelopmentSettingsConfigureLocalWorker()
  {
    var wwwrootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
      "../../../../../MyTransportAppWASM/wwwroot"));
    var prodPath = Path.Combine(wwwrootDir, "appsettings.json");
    var devPath = Path.Combine(wwwrootDir, "appsettings.Development.example.json");

    var prodSettings = new ConfigurationBuilder().AddJsonFile(prodPath).Build();
    var prodProviders = prodSettings.GetSection("TransportProviders").Get<List<TransportProvider>>()!;
    var staticOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Assert.All(prodProviders, p =>
    {
      var baseUrl = p.StaticData?.BaseUrl ?? string.Empty;
      Assert.DoesNotContain("localhost", baseUrl, StringComparison.OrdinalIgnoreCase);
      if (p.HasStaticData)
      {
        Assert.True(Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.False(uri.IsLoopback);
        Assert.NotEqual("/", uri.AbsolutePath);
        staticOrigins.Add(uri.GetLeftPart(UriPartial.Authority));
      }
    });
    Assert.Single(staticOrigins);
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
      ["TransportProviders:0:StaticData:GeoJSONUrlPath"] = Source.GeoJSONUrlPath
    }).Build();

    var provider = Assert.Single(settings.GetSection("TransportProviders").Get<List<TransportProvider>>()!);
    Assert.Equal(Source, provider.StaticData);
  }

  [Fact]
  public async Task ReadsConfiguredRoutesAndMap()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => """{"data":[{"route_id":"A 1","route_short_name":"A1","route_long_name":"Airport"},{"route_id":"B2","route_long_name":"Downtown"}],"limit":1000,"offset":0}""",
      "/rapid-bus-kl/map" => """{"type":"FeatureCollection","features":[]}""",
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var routes = await service.GetRoutesAsync(Source);
    var allMap = await service.GetMapAsync(Source);
    var map = await service.GetMapAsync(Source, "A 1");

    Assert.Equal([new StaticRoute("A 1", "A1", "Airport"), new StaticRoute("B2", null, "Downtown")], routes);
    Assert.Contains("FeatureCollection", allMap);
    Assert.Contains("FeatureCollection", map);
    Assert.Equal("https://worker.example/rapid-bus-kl/data/routes?include=route_id,route_short_name,route_long_name&sort=route_id&limit=1000&offset=0", handler.Urls[0]);
    Assert.Equal("https://worker.example/rapid-bus-kl/map", handler.Urls[1]);
    Assert.Equal("https://worker.example/rapid-bus-kl/map?route_id=A%201", handler.Urls[2]);
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
  public async Task CoalescesConcurrentRouteAndMapFetchesWithoutRetainingRamCache()
  {
    const string mapPayload = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"LineString","coordinates":[]},"properties":{"kind":"route","route_id":"A1","route_short_name":"Airport"}}]}""";
    var mapFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var mapGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var mapFetchCount = 0;
    var handler = new AsyncFakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/data/routes" => throw new HttpRequestException("CORS blocked"),
      "/rapid-bus-kl/map" => Interlocked.Increment(ref mapFetchCount) == 1
        ? WaitForFirstMapAsync(mapFetchStarted, mapGate)
        : Task.FromResult(mapPayload),
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    var routesTask1 = service.GetRoutesAsync(Source);
    var routesTask2 = service.GetRoutesAsync(Source);
    await mapFetchStarted.Task;
    var mapTask = service.GetMapAsync(Source);
    mapGate.SetResult(mapPayload);
    await Task.WhenAll(routesTask1, routesTask2, mapTask);

    Assert.Equal(new StaticRoute("A1", "Airport"), Assert.Single(await routesTask1));
    Assert.Equal(new StaticRoute("A1", "Airport"), Assert.Single(await routesTask2));
    Assert.Equal(mapPayload, await mapTask);
    Assert.Equal(1, handler.Urls.Count(u => u.EndsWith("/data/routes", StringComparison.Ordinal) || u.Contains("/data/routes?", StringComparison.Ordinal)));
    Assert.Equal(1, handler.Urls.Count(u => u.EndsWith("/map", StringComparison.Ordinal)));

    // Subsequent sequential calls delegate caching to the Service Worker (static-data-cache-v1).
    var routesAgain = await service.GetRoutesAsync(Source);
    var mapAgain = await service.GetMapAsync(Source);
    Assert.Single(routesAgain);
    Assert.Equal(mapPayload, mapAgain);
    Assert.Equal(3, handler.Urls.Count(u => u.EndsWith("/map", StringComparison.Ordinal)));
  }

  private static async Task<string> WaitForFirstMapAsync(
    TaskCompletionSource started,
    TaskCompletionSource<string> gate)
  {
    started.TrySetResult();
    return await gate.Task;
  }

  [Fact]
  public async Task ReturnsEmptyImmediatelyWhenBaseUrlIsWhitespace()
  {
    var emptySource = new TransportStaticData("", "/data/routes", "/map");
    var handler = new FakeHandler(_ => throw new InvalidOperationException("No network calls expected"));
    var service = new TransportStaticDataService(new HttpClient(handler));

    var routes = await service.GetRoutesAsync(emptySource);
    var map = await service.GetMapAsync(emptySource);

    Assert.Empty(routes);
    Assert.Empty(map);
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
  public async Task TrimsMapRouteIdQueryParameter()
  {
    var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-bus-kl/map" => """{"type":"FeatureCollection","features":[]}""",
      _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    await service.GetMapAsync(Source, "   ");
    await service.GetMapAsync(Source, " A 1 ");

    Assert.Equal(
    [
      "https://worker.example/rapid-bus-kl/map",
      "https://worker.example/rapid-bus-kl/map?route_id=A%201"
    ], handler.Urls);
  }

  [Fact]
  public async Task ReadsAndCoalescesFrequenciesAndDateScopedMapsAndSkipsWhenPathMissing()
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
      "https://worker.example/rapid-rail-kl", "/data/routes", "/map", "/data/frequencies");
    var freqStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var freqGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new AsyncFakeHandler(request => request.RequestUri!.AbsolutePath switch
    {
      "/rapid-rail-kl/data/frequencies" => WaitForFirstMapAsync(freqStarted, freqGate),
      "/rapid-rail-kl/map" => Task.FromResult("""{"type":"FeatureCollection","features":[]}"""),
      _ => throw new InvalidOperationException(request.RequestUri.ToString())
    });
    var service = new TransportStaticDataService(new HttpClient(handler));

    Assert.Empty(await service.GetFrequenciesAsync(Source)); // FrequenciesPath is empty on bus source

    var firstTask = service.GetFrequenciesAsync(railSource);
    await freqStarted.Task;
    var secondTask = service.GetFrequenciesAsync(railSource);
    freqGate.SetResult(frequenciesPayload);
    var first = await firstTask;
    var second = await secondTask;
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

  private sealed class AsyncFakeHandler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
  {
    public List<string> Urls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      Urls.Add(request.RequestUri!.AbsoluteUri);
      return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(await respond(request)) };
    }
  }
}
