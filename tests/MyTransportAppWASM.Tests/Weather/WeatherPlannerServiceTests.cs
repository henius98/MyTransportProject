using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

namespace MyTransportAppWASM.Tests;

public class WeatherPlannerServiceTests
{
  private static readonly Uri Endpoint = new("https://weather.example.test/forecast");

  [Fact]
  public async Task GetPlanAsync_CoalescesConcurrentRequestsAndLabelsCachedRealtimeForecasts()
  {
    var (service, handler, cache) = CreateHarness(delay: TimeSpan.FromMilliseconds(50));
    using var _ = cache;
    var query = KlQuery();

    var plans = await Task.WhenAll(service.GetPlanAsync(query), service.GetPlanAsync(query));
    var cachedPlan = await service.GetPlanAsync(query);

    var first = plans[0].GetRealtime("Open-Meteo")!;
    var second = plans[1].GetRealtime("Open-Meteo")!;
    var cached = cachedPlan.GetRealtime("Open-Meteo")!;

    Assert.Equal(1, handler.RequestCount);
    Assert.Same(first.Periods, second.Periods);
    Assert.Same(first.Periods, cached.Periods);
    Assert.Equal("LIVE", first.Status);
    Assert.Equal("LIVE (Cached)", second.Status);
    Assert.Equal("LIVE (Cached)", cached.Status);
    Assert.Equal(WeatherBadgeTone.Live, first.Tone);
    Assert.Equal(WeatherBadgeTone.Live, cached.Tone);
    Assert.False(first.IsCached);
    Assert.True(second.IsCached);
    Assert.True(cached.IsCached);
  }

  [Fact]
  public async Task GetPlanAsync_PreservesFailureStatusAndCachesBriefly()
  {
    var (service, handler, cache) = CreateHarness(status: HttpStatusCode.ServiceUnavailable, json: "{}");
    using var _ = cache;
    var query = KlQuery();

    var first = (await service.GetPlanAsync(query)).GetRealtime("Open-Meteo")!;
    var second = (await service.GetPlanAsync(query)).GetRealtime("Open-Meteo")!;

    Assert.Equal(1, handler.RequestCount);
    Assert.Equal("HTTP 503", first.Status);
    Assert.Equal("HTTP 503 (Cached)", second.Status);
    Assert.Equal(WeatherBadgeTone.Error, first.Tone);
    Assert.Equal(WeatherBadgeTone.Error, second.Tone);
  }

  [Theory]
  [InlineData(false, "?current_weather=true", "LIVE")]
  [InlineData(false, "", "LIVE")]
  [InlineData(true, "", "FORECAST")]
  public async Task GetPlanAsync_AssignsStatusForWeatherType(
      bool isOutlook,
      string querySuffix,
      string expectedStatus)
  {
    var (service, _, cache) = CreateHarness(BuildSingleProviderOptions(querySuffix));
    using var _ = cache;
    var query = KlQuery(@return: isOutlook ? MalaysiaTime.At(MalaysiaTime.Today.AddDays(1)) : null);

    var plan = await service.GetPlanAsync(query);
    var result = isOutlook ? plan.GetOutlook("Open-Meteo")! : plan.GetRealtime("Open-Meteo")!;

    Assert.Equal(expectedStatus, result.Status);
  }

  [Fact]
  public async Task GetPlanAsync_CachesOutlookSeparatelyFromRealtime()
  {
    var (service, handler, cache) = CreateHarness();
    using var _ = cache;
    var fullQuery = KlQuery(@return: MalaysiaTime.At(MalaysiaTime.Today.AddDays(1)));

    await service.GetPlanAsync(KlQuery());
    var outlook = (await service.GetPlanAsync(fullQuery)).GetOutlook("Open-Meteo")!;
    var cachedOutlook = (await service.GetPlanAsync(fullQuery)).GetOutlook("Open-Meteo")!;

    Assert.Equal(2, handler.RequestCount);
    Assert.Equal("FORECAST", outlook.Status);
    Assert.False(outlook.IsCached);
    Assert.Equal("FORECAST (Cached)", cachedOutlook.Status);
    Assert.True(cachedOutlook.IsCached);
  }

  [Fact]
  public async Task GetPlanAsync_PreservesMetMalaysiaDayPartLabels()
  {
    var (service, _, cache) = CreateHarness(BuildMetMalaysiaOptions(), "[" + BuildMetMalaysiaDayJson(0) + "]");
    using var _ = cache;

    var plan = await service.GetPlanAsync(KlQuery("Ds001", @return: MalaysiaTime.At(MalaysiaTime.Today.AddDays(1))));
    var outlook = plan.GetOutlook("MetMalaysia")!;

    Assert.Collection(
        outlook.Periods,
        morning => Assert.Equal("Pagi", morning.Label),
        afternoon => Assert.Equal("Petang", afternoon.Label),
        night => Assert.Equal("Malam", night.Label));
  }

  [Fact]
  public async Task GetPlanAsync_RetainsAll14DaysOfMetMalaysiaPeriodsWhenThreePeriodsPerDay()
  {
    var metJson = "[" + string.Join(",", Enumerable.Range(0, 15).Select(BuildMetMalaysiaDayJson)) + "]";
    var (service, _, cache) = CreateHarness(BuildMetMalaysiaOptions(), metJson);
    using var _ = cache;

    var plan = await service.GetPlanAsync(KlQuery("Ds001", @return: MalaysiaTime.At(MalaysiaTime.Today.AddDays(13))));
    var outlook = plan.GetOutlook("MetMalaysia")!;

    Assert.Equal(42, outlook.Periods.Count);
    Assert.Equal(14, outlook.Periods.Select(p => MalaysiaTime.Convert(p.Time).Date).Distinct().Count());
  }

  [Fact]
  public async Task GetHourlyForecastAsync_ExpiresOutlookAfterThreeHours()
  {
    var clock = new TestClock();
    var (service, handler, cache) = CreateHarness(cacheOptions: new MemoryCacheOptions { Clock = clock });
    using var _ = cache;
    var query = KlQuery();
    var date = new DateTime(2026, 8, 26);

    await service.GetHourlyForecastAsync(query, date);
    clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(59));
    var cached = await service.GetHourlyForecastAsync(query, date);
    clock.Advance(TimeSpan.FromMinutes(2));
    var refreshed = await service.GetHourlyForecastAsync(query, date);

    Assert.Equal(2, handler.RequestCount);
    Assert.True(cached.IsCached);
    Assert.False(refreshed.IsCached);
  }

  [Fact]
  public async Task GetPlanAsync_UsesCanonicalAdapterName_WhenOptionsNameIsBlank()
  {
    var options = BuildTestWeatherOptions();
    options.OpenMeteo.Name = "";
    var (service, _, cache) = CreateHarness(options);
    using var _ = cache;
    var query = KlQuery(@return: MalaysiaTime.At(MalaysiaTime.Today.AddDays(1)));

    var plan = await service.GetPlanAsync(query);

    Assert.NotNull(plan.GetRealtime("Open-Meteo"));
    Assert.NotNull(plan.GetOutlook("Open-Meteo"));
    var refreshed = await service.RefreshProviderAsync("Open-Meteo", query, isOutlook: false);
    Assert.Equal("Open-Meteo", refreshed.Provider);
  }

  [Fact]
  public async Task GetHourlyForecastAsync_ForceRefreshBypassesCachedProviderResult()
  {
    var (service, handler, cache) = CreateHarness();
    using var _ = cache;
    var query = KlQuery();
    var date = new DateTime(2026, 8, 26);

    var initial = await service.GetHourlyForecastAsync(query, date);
    var cached = await service.GetHourlyForecastAsync(query, date);
    var refreshed = await service.GetHourlyForecastAsync(query, date, forceRefresh: true);

    Assert.Equal(2, handler.RequestCount);
    Assert.False(initial.IsCached);
    Assert.True(cached.IsCached);
    Assert.False(refreshed.IsCached);
    Assert.Equal("FORECAST", refreshed.Status);
  }

  [Fact]
  public async Task GetPlanAsync_TimesOutWhileReadingResponseBody()
  {
    using var cache = new MemoryCache(new MemoryCacheOptions());
    using var client = new HttpClient(new HangingContentHandler())
    {
      Timeout = TimeSpan.FromMilliseconds(50)
    };
    var service = CreateService(client, cache, BuildSingleProviderOptions());

    var plan = await service
        .GetPlanAsync(KlQuery())
        .WaitAsync(TimeSpan.FromSeconds(2));

    Assert.Equal("Unavailable", plan.GetRealtime("Open-Meteo")!.Status);
  }

  [Fact]
  public async Task GetPlanAsync_ReturnsConsolidatedPlan_WithRealtimeAndOutlooks()
  {
    var (service, _, cache) = CreateHarness(BuildTestWeatherOptions());
    using var _ = cache;
    var query = KlQuery(
        "Ds001",
        MalaysiaTime.At(MalaysiaTime.Today),
        MalaysiaTime.At(MalaysiaTime.Today.AddDays(3)));

    var plan = await service.GetPlanAsync(query);

    Assert.NotNull(plan);
    Assert.Equal(query, plan.Query);
    Assert.Equal(2, plan.Realtime.Count);
    Assert.Equal(2, plan.Outlooks.Count);
    Assert.NotNull(plan.GetRealtime("MetMalaysia"));
    Assert.NotNull(plan.GetRealtime("Open-Meteo"));
    Assert.Null(plan.GetRealtime("Singapore NEA"));
    Assert.NotNull(plan.GetOutlook("MetMalaysia"));
    Assert.NotNull(plan.GetOutlook("Open-Meteo"));
    Assert.Null(plan.GetOutlook("Singapore NEA"));
  }

  [Fact]
  public async Task GetPlanAsync_GeoBoundsSingaporeNea_IncludedWhenInBounds_ExcludedWhenOutside()
  {
    var (service, _, cache) = CreateHarness(BuildTestWeatherOptions());
    using var _ = cache;
    var nextDay = MalaysiaTime.At(MalaysiaTime.Today.AddDays(1));
    var singaporeQuery = new WeatherQuery { Latitude = 1.3521, Longitude = 103.8198, Return = nextDay };

    var singaporePlan = await service.GetPlanAsync(singaporeQuery);
    var klPlan = await service.GetPlanAsync(KlQuery(@return: nextDay));

    Assert.NotNull(singaporePlan.GetRealtime("Singapore NEA"));
    Assert.NotNull(singaporePlan.GetOutlook("Singapore NEA"));
    Assert.Null(klPlan.GetRealtime("Singapore NEA"));
    Assert.Null(klPlan.GetOutlook("Singapore NEA"));
  }

  [Fact]
  public async Task GetHourlyForecastAsync_FiltersAndSortsPeriodsForRequestedDate()
  {
    const string hourlyJson = """
            {
              "hourly": {
                "time": [
                  "2026-08-26T10:00:00+08:00",
                  "2026-08-26T08:00:00+08:00",
                  "2026-08-27T08:00:00+08:00"
                ],
                "temperature_2m": [31.5, 28.0, 29.0],
                "precipitation_probability": [50, 10, 0],
                "weather_code": [2, 1, 0]
              }
            }
            """;
    var (service, _, cache) = CreateHarness(BuildTestWeatherOptions(), hourlyJson);
    using var _ = cache;
    var targetDate = new DateTime(2026, 8, 26);

    var result = await service.GetHourlyForecastAsync(KlQuery(), targetDate);
    var slices = result.Periods;

    Assert.Equal(2, slices.Count);
    Assert.Equal(28.0, slices[0].TemperatureC);
    Assert.Equal(31.5, slices[1].TemperatureC);
    Assert.All(slices, s => Assert.Equal(targetDate.Date, MalaysiaTime.Convert(s.Time).Date));
  }

  [Fact]
  public async Task RefreshProviderAsync_RefreshesOnlySpecifiedProvider()
  {
    var (service, handler, cache) = CreateHarness(BuildTestWeatherOptions());
    using var _ = cache;
    var query = KlQuery();

    var result = await service.RefreshProviderAsync("Open-Meteo", query, isOutlook: false);

    Assert.Equal("Open-Meteo", result.Provider);
    Assert.Equal(1, handler.RequestCount);

    await Assert.ThrowsAsync<ArgumentException>(() =>
        service.RefreshProviderAsync("UnknownProvider", query, isOutlook: false));
  }

  [Fact]
  public async Task FetchWeatherAsync_WithBoundedMemoryCache_SucceedsWithSizeLimit()
  {
    var (service, handler, cache) = CreateHarness(
        BuildTestWeatherOptions(),
        cacheOptions: new MemoryCacheOptions { SizeLimit = 10 });
    using var _ = cache;
    var query = KlQuery();

    Assert.NotNull(await service.GetPlanAsync(query, forceRefresh: false));
    Assert.Equal(1, handler.RequestCount);

    // Subsequent call hits cache with SizeLimit without throwing
    Assert.NotNull(await service.GetPlanAsync(query, forceRefresh: false));
    Assert.Equal(1, handler.RequestCount);
  }

  private static WeatherQuery KlQuery(
      string? locationId = null,
      DateTimeOffset? departure = null,
      DateTimeOffset? @return = null) => new()
      {
        Latitude = 3.139,
        Longitude = 101.6869,
        LocationId = locationId,
        Departure = departure,
        Return = @return
      };

  private static string BuildMetMalaysiaDayJson(int offset) => $$"""
        {
          "date": "{{MalaysiaTime.Today.AddDays(offset):yyyy-MM-dd}}T00:00:00Z",
          "morning_forecast": "Tiada hujan",
          "afternoon_forecast": "Ribut petir di beberapa tempat",
          "night_forecast": "Tiada hujan"
        }
        """;

  private static WeatherOptions BuildMetMalaysiaOptions() => new()
  {
    MetMalaysia = new()
    {
      Enabled = true,
      Name = "MetMalaysia",
      Source = "data.gov.my",
      BaseUrl = Endpoint.AbsoluteUri,
      Endpoints = string.Empty
    }
  };

  private static (WeatherPlannerService Service, CountingHandler Handler, MemoryCache Cache) CreateHarness(
      WeatherOptions? weatherOptions = null,
      string json = WeatherJson,
      HttpStatusCode status = HttpStatusCode.OK,
      TimeSpan delay = default,
      MemoryCacheOptions? cacheOptions = null)
  {
    var handler = new CountingHandler(status, json, delay);
    var cache = new MemoryCache(cacheOptions ?? new MemoryCacheOptions());
    var service = CreateService(new HttpClient(handler), cache, weatherOptions ?? BuildSingleProviderOptions());
    return (service, handler, cache);
  }

  private static WeatherOptions BuildSingleProviderOptions(string endpointSuffix = "") => new()
  {
    OpenMeteo = new()
    {
      Enabled = true,
      Name = "Open-Meteo",
      Source = "test",
      BaseUrl = Endpoint.AbsoluteUri,
      Endpoints = new()
      {
        ["Realtime"] = endpointSuffix,
        ["Outlook"] = endpointSuffix,
        ["Hourly"] = endpointSuffix
      }
    }
  };

  private static WeatherOptions BuildTestWeatherOptions() => new()
  {
    MetMalaysia = new()
    {
      Enabled = true,
      Name = "MetMalaysia",
      Source = "data.gov.my",
      BaseUrl = "https://weather.example.test/met",
      Endpoints = "?contains={location_id}"
    },
    OpenMeteo = new()
    {
      Enabled = true,
      Name = "Open-Meteo",
      Source = "open-meteo.com",
      BaseUrl = "https://weather.example.test/openmeteo",
      Endpoints = new()
      {
        ["Realtime"] = "?lat={lat}&lon={lon}",
        ["Outlook"] = "?lat={lat}&lon={lon}&daily=true",
        ["Hourly"] = "?lat={lat}&lon={lon}&start_date={date}&end_date={date}&timezone={timezone}"
      }
    },
    SingaporeNEA = new()
    {
      Enabled = true,
      Name = "Singapore NEA",
      Source = "data.gov.sg",
      BaseUrl = "https://weather.example.test/singapore",
      Endpoints = new()
      {
        ["Realtime"] = "/two-hr-forecast",
        ["Outlook"] = "/four-day-outlook"
      }
    }
  };

  private static WeatherPlannerService CreateService(
      HttpClient client,
      IMemoryCache cache,
      WeatherOptions? weatherOptions = null) =>
      new(client, cache, Options.Create(new CacheExpirationOptions
      {
        Weather = new()
        {
          LiveSeconds = 600,
          OutlookSeconds = 10800,
          FallbackSeconds = 30
        }
      }), Options.Create(weatherOptions ?? new WeatherOptions()));

  private const string WeatherJson = """
        {
          "hourly": {
            "time": ["2026-08-26T00:00:00Z"],
            "temperature_2m": [30],
            "precipitation_probability": [40],
            "weather_code": [2]
          }
        }
        """;

  private sealed class CountingHandler(
      HttpStatusCode statusCode,
      string responseBody,
      TimeSpan delay = default) : HttpMessageHandler
  {
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _requestCount);
      if (delay > TimeSpan.Zero)
      {
        await Task.Delay(delay, cancellationToken);
      }

      return new HttpResponseMessage(statusCode)
      {
        Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
      };
    }
  }

  private sealed class TestClock : ISystemClock
  {
    public DateTimeOffset UtcNow { get; private set; } =
        new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan duration) => UtcNow += duration;
  }

  private sealed class HangingContentHandler : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StreamContent(new HangingReadStream())
        });
  }

  private sealed class HangingReadStream : Stream
  {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WaitForCancellationAsync(cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        new(WaitForCancellationAsync(cancellationToken));

    private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return 0;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
