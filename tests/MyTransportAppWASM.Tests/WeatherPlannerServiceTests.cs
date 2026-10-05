using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class WeatherPlannerServiceTests
{
    private static readonly Uri Endpoint = new("https://weather.example.test/forecast");

    [Fact]
    public async Task FetchProviderDataAsync_CoalescesConcurrentRequestsAndLabelsCachedRealtimeForecasts()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, WeatherJson, delay: TimeSpan.FromMilliseconds(50));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(new HttpClient(handler), cache);
        var provider = BuildProvider();

        var firstTask = service.FetchProviderDataAsync(provider, Endpoint, "Realtime");
        var secondTask = service.FetchProviderDataAsync(provider, Endpoint, "Realtime");
        var results = await Task.WhenAll(firstTask, secondTask);
        var cached = await service.FetchProviderDataAsync(provider, Endpoint, "Realtime");

        Assert.Equal(1, handler.RequestCount);
        Assert.Same(results[0].Periods, results[1].Periods);
        Assert.Same(results[0].Periods, cached.Periods);
        Assert.Equal("LIVE", results[0].Status);
        Assert.Equal("LIVE (Cached)", results[1].Status);
        Assert.Equal("LIVE (Cached)", cached.Status);
        Assert.False(results[0].IsCached);
        Assert.True(results[1].IsCached);
        Assert.True(cached.IsCached);
    }

    [Fact]
    public async Task FetchProviderDataAsync_PreservesFailureStatusAndCachesBriefly()
    {
        var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable, "{}");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(new HttpClient(handler), cache);
        var provider = BuildProvider();

        var first = await service.FetchProviderDataAsync(provider, Endpoint, "Realtime");
        var second = await service.FetchProviderDataAsync(provider, Endpoint, "Realtime");

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("HTTP 503", first.Status);
        Assert.Equal("HTTP 503 (Cached)", second.Status);
    }

    [Theory]
    [InlineData("Realtime", "https://weather.example.test/forecast?current_weather=true", "LIVE")]
    [InlineData("Realtime", "https://weather.example.test/forecast", "LIVE")]
    [InlineData("Outlook", "https://weather.example.test/forecast", "FORECAST")]
    public async Task FetchProviderDataAsync_AssignsStatusForWeatherType(
        string label,
        string endpoint,
        string expectedStatus)
    {
        var handler = new CountingHandler(HttpStatusCode.OK, WeatherJson);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(new HttpClient(handler), cache);

        var result = await service.FetchProviderDataAsync(BuildProvider(), new Uri(endpoint), label);

        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public async Task FetchProviderDataAsync_CachesOutlookSeparatelyFromRealtime()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, WeatherJson);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(new HttpClient(handler), cache);

        await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Realtime");
        var outlook = await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Outlook");
        var cachedOutlook = await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Outlook");

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("FORECAST", outlook.Status);
        Assert.False(outlook.IsCached);
        Assert.Equal("FORECAST (Cached)", cachedOutlook.Status);
        Assert.True(cachedOutlook.IsCached);
    }

    [Theory]
    [InlineData("Realtime")]
    [InlineData("Outlook")]
    public async Task FetchProviderDataAsync_PreservesMetMalaysiaDayPartLabels(string label)
    {
        var handler = new CountingHandler(HttpStatusCode.OK, MetMalaysiaWeatherJson);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(new HttpClient(handler), cache);

        var result = await service.FetchProviderDataAsync(BuildProvider(), Endpoint, label);

        Assert.Collection(
            result.Periods,
            morning => Assert.Equal("Pagi", morning.Label),
            afternoon => Assert.Equal("Petang", afternoon.Label),
            night => Assert.Equal("Malam", night.Label));
    }

    [Fact]
    public async Task FetchProviderDataAsync_ExpiresOutlookAfterThreeHours()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, WeatherJson);
        var clock = new TestClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var service = CreateService(new HttpClient(handler), cache);

        await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Outlook");
        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(59));
        var cached = await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Outlook");
        clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Outlook");

        Assert.Equal(2, handler.RequestCount);
        Assert.True(cached.IsCached);
        Assert.False(refreshed.IsCached);
    }

    [Fact]
    public async Task FetchProviderDataAsync_ForceRefreshBypassesCachedProviderResult()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, WeatherJson);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(new HttpClient(handler), cache);

        var initial = await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Outlook");
        var cached = await service.FetchProviderDataAsync(BuildProvider(), Endpoint, "Outlook");
        var refreshed = await service.FetchProviderDataAsync(
            BuildProvider(),
            Endpoint,
            "Outlook",
            forceRefresh: true);

        Assert.Equal(2, handler.RequestCount);
        Assert.False(initial.IsCached);
        Assert.True(cached.IsCached);
        Assert.False(refreshed.IsCached);
        Assert.Equal("FORECAST", refreshed.Status);
    }

    [Fact]
    public async Task FetchProviderDataAsync_TimesOutWhileReadingResponseBody()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new HangingContentHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        var service = CreateService(client, cache);

        var result = await service
            .FetchProviderDataAsync(BuildProvider(), Endpoint, "Realtime")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Unavailable", result.Status);
    }

    private static WeatherProviderOptions BuildProvider() => new()
    {
        Enabled = true,
        Name = "Test Weather",
        Source = "test"
    };

    private static WeatherPlannerService CreateService(HttpClient client, IMemoryCache cache) =>
        new(client, cache, Options.Create(new CacheExpirationOptions
        {
            Weather = new()
            {
                LiveSeconds = 600,
                OutlookSeconds = 10800,
                FallbackSeconds = 30
            }
        }));

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

    private const string MetMalaysiaWeatherJson = """
        [
          {
            "date": "2026-09-22T00:00:00Z",
            "morning_forecast": "Tiada hujan",
            "afternoon_forecast": "Ribut petir di beberapa tempat",
            "night_forecast": "Tiada hujan"
          }
        ]
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
