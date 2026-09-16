using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class WeatherPlannerServiceTests
{
    private static readonly Uri Endpoint = new("https://weather.example.test/forecast");

    [Fact]
    public async Task FetchProviderDataAsync_CoalescesConcurrentRequestsAndReusesPeriods()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, WeatherJson, delay: TimeSpan.FromMilliseconds(50));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new WeatherPlannerService(new HttpClient(handler), cache);
        var provider = BuildProvider();

        var firstTask = service.FetchProviderDataAsync(provider, Endpoint, "Baseline");
        var secondTask = service.FetchProviderDataAsync(provider, Endpoint, "Baseline");
        var results = await Task.WhenAll(firstTask, secondTask);
        var cached = await service.FetchProviderDataAsync(provider, Endpoint, "Baseline");

        Assert.Equal(1, handler.RequestCount);
        Assert.Same(results[0].Periods, results[1].Periods);
        Assert.Same(results[0].Periods, cached.Periods);
        Assert.Equal("Live", cached.Status);
    }

    [Fact]
    public async Task FetchProviderDataAsync_PreservesFailureStatusAndCachesBriefly()
    {
        var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable, "{}");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new WeatherPlannerService(new HttpClient(handler), cache);
        var provider = BuildProvider();

        var first = await service.FetchProviderDataAsync(provider, Endpoint, "Baseline");
        var second = await service.FetchProviderDataAsync(provider, Endpoint, "Baseline");

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("HTTP 503", first.Status);
        Assert.Equal("HTTP 503", second.Status);
    }

    [Fact]
    public async Task FetchProviderDataAsync_TimesOutWhileReadingResponseBody()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new HangingContentHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        var service = new WeatherPlannerService(client, cache);

        var result = await service
            .FetchProviderDataAsync(BuildProvider(), Endpoint, "Baseline")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Unavailable", result.Status);
    }

    private static WeatherProviderOptions BuildProvider() => new()
    {
        Enabled = true,
        Name = "Test Weather",
        Source = "test"
    };

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
