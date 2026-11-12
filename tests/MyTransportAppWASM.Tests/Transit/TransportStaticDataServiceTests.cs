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
        Assert.StartsWith("https://", baseUrl, StringComparison.OrdinalIgnoreCase);
      }
    });
    Assert.DoesNotContain("localhost", prodSettings["BaziFlow:BaseUrl"] ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    var devSettings = new ConfigurationBuilder()
      .AddJsonFile(prodPath)
      .AddJsonFile(devPath)
      .Build();
    var railProvider = Assert.Single(devSettings.GetSection("TransportProviders").Get<List<TransportProvider>>()!,
      provider => provider.Name == "Rapid Rail KL");

    Assert.False(railProvider.HasRealtimeFeed);
    Assert.True(railProvider.HasStaticData);
    Assert.Equal("Rail", railProvider.TransportType);
    Assert.Equal("http://localhost:8787/rapid-rail-kl", railProvider.StaticData?.BaseUrl);
    Assert.Equal("/departures", railProvider.StaticData?.DeparturePath);
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
      "/rapid-bus-kl/data/routes" => """{"data":[{"route_id":"A 1","route_short_name":"A1","route_long_name":"Airport"}],"limit":1000,"offset":0}""",
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

    Assert.Equal(new StaticRoute("A 1", "A1"), Assert.Single(routes));
    Assert.Contains("FeatureCollection", allMap);
    Assert.Contains("FeatureCollection", map);
    Assert.Equal("Airport", Assert.Single(allDepartures).Headsign);
    Assert.Equal("Airport", Assert.Single(departures).Headsign);
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
    Assert.Equal(default, departures[0].DepartureAt);
    Assert.Equal("B2", departures[1].RouteId);
    Assert.Null(departures[1].Headsign);
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
