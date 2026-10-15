using System.Net;
using System.Text;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class WeatherStationCatalogServiceTests
{
  [Fact]
  public async Task GetAllStationsAsync_CoalescesConcurrentLoadsAndCachesStations()
  {
    var handler = new TestHttpHandler(
        """
            [
              { "location_id": "loc-1", "location_name": "Kuala Lumpur", "latitude": 3.139, "longitude": 101.687 },
              { "location_id": "loc-2", "location_name": "Johor Bahru", "latitude": 1.4927, "longitude": 103.7414 }
            ]
            """,
        delay: TimeSpan.FromMilliseconds(25));
    var client = new HttpClient(handler) { BaseAddress = new Uri("https://app.test/") };
    var service = new WeatherStationCatalogService(client);

    var concurrentFirstTask = service.GetAllStationsAsync();
    var concurrentSecondTask = service.GetAllStationsAsync();
    await Task.WhenAll(concurrentFirstTask, concurrentSecondTask);
    var firstCall = await concurrentFirstTask;
    var secondCall = await concurrentSecondTask;
    var cachedCall = await service.GetAllStationsAsync();

    Assert.Equal(2, firstCall.Count);
    Assert.Same(firstCall, secondCall);
    Assert.Same(firstCall, cachedCall);
    Assert.Equal(1, handler.RequestCount);
  }

  [Fact]
  public async Task FindNearestStationAsync_ReturnsClosestStationUsingGeoMath()
  {
    var handler = new TestHttpHandler(
        """
            [
              { "location_id": "KL", "location_name": "Kuala Lumpur", "latitude": 3.139, "longitude": 101.687 },
              { "location_id": "JB", "location_name": "Johor Bahru", "latitude": 1.4927, "longitude": 103.7414 },
              { "location_id": "PG", "location_name": "Penang", "latitude": 5.414, "longitude": 100.329 }
            ]
            """);
    var client = new HttpClient(handler) { BaseAddress = new Uri("https://app.test/") };
    var service = new WeatherStationCatalogService(client);

    // Coordinates very close to KL
    var nearest = await service.FindNearestStationAsync(3.14, 101.69);

    Assert.NotNull(nearest);
    Assert.Equal("KL", nearest.LocationId);
    Assert.Equal("Kuala Lumpur", nearest.LocationName);
  }

  [Fact]
  public async Task SearchStationsAsync_MatchesNameAndIdCaseInsensitive()
  {
    var handler = new TestHttpHandler(
        """
            [
              { "location_id": "KL001", "location_name": "Kuala Lumpur Central", "latitude": 3.139, "longitude": 101.687 },
              { "location_id": "JB002", "location_name": "Johor Bahru", "latitude": 1.4927, "longitude": 103.7414 }
            ]
            """);
    var client = new HttpClient(handler) { BaseAddress = new Uri("https://app.test/") };
    var service = new WeatherStationCatalogService(client);

    var byName = await service.SearchStationsAsync("  central  ");
    var byId = await service.SearchStationsAsync(" jb002 ");

    Assert.Single(byName);
    Assert.Equal("KL001", byName[0].LocationId);

    Assert.Single(byId);
    Assert.Equal("JB002", byId[0].LocationId);
  }

  [Fact]
  public async Task TransientFailure_AllowsSubsequentRetry()
  {
    var handler = new FlakyHttpHandler(failFirstTime: true);
    var client = new HttpClient(handler) { BaseAddress = new Uri("https://app.test/") };
    var service = new WeatherStationCatalogService(client);

    // First call fails internally and returns empty
    var firstResult = await service.GetAllStationsAsync();
    Assert.Empty(firstResult);

    // Second call retries and succeeds
    var secondResult = await service.GetAllStationsAsync();
    Assert.Single(secondResult);
    Assert.Equal("loc-1", secondResult[0].LocationId);
  }

  [Fact]
  public async Task CancelledCaller_DoesNotFaultSharedStationTask()
  {
    var handler = new TestHttpHandler(
        """[{"location_id":"loc-1","location_name":"Test","latitude":3.0,"longitude":101.0}]""");
    var client = new HttpClient(handler) { BaseAddress = new Uri("https://app.test/") };
    var service = new WeatherStationCatalogService(client);

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // First call is cancelled
    var cancelledResult = await service.GetAllStationsAsync(cts.Token);
    Assert.Empty(cancelledResult);

    // Subsequent caller should still succeed
    var subsequentResult = await service.GetAllStationsAsync();
    Assert.Single(subsequentResult);
    Assert.Equal("loc-1", subsequentResult[0].LocationId);
  }

  private sealed class TestHttpHandler : HttpMessageHandler
  {
    private readonly string _json;
    private readonly TimeSpan _delay;
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public TestHttpHandler(string json, TimeSpan delay = default)
    {
      _json = json;
      _delay = delay;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _requestCount);
      if (_delay > TimeSpan.Zero)
      {
        await Task.Delay(_delay, cancellationToken);
      }

      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(_json, Encoding.UTF8, "application/json")
      };
    }
  }

  private sealed class FlakyHttpHandler : HttpMessageHandler
  {
    private bool _failed;
    private readonly bool _failFirstTime;

    public FlakyHttpHandler(bool failFirstTime) => _failFirstTime = failFirstTime;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      if (_failFirstTime && !_failed)
      {
        _failed = true;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
      }

      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(
              """[{"location_id":"loc-1","location_name":"Test","latitude":3.0,"longitude":101.0}]""",
              Encoding.UTF8,
              "application/json")
      });
    }
  }
}
