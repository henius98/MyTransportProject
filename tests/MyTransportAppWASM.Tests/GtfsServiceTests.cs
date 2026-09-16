using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;
using TransitRealtime;

namespace MyTransportAppWASM.Tests;

public sealed class GtfsServiceTests
{
  private const string FeedUrl = "https://transport.example.test/vehicle-positions";

  [Fact]
  public async Task GetBusPositionsAsync_RevalidatesWithHttpValidators()
  {
    var lastModified = new DateTimeOffset(2026, 8, 28, 1, 2, 3, TimeSpan.Zero);
    var handler = new RecordingHandler(
      _ => FeedResponse("vehicle-1", entityTag: "\"feed-v1\"", lastModified),
      _ => new HttpResponseMessage(HttpStatusCode.NotModified));
    var service = new GtfsService(new HttpClient(handler));

    var initial = await service.GetBusPositionsAsync(FeedUrl, allowNotModified: false);
    var revalidated = await service.GetBusPositionsAsync(FeedUrl, allowNotModified: true);

    Assert.Equal(GtfsFetchStatus.Updated, initial.Status);
    Assert.Equal("vehicle-1", initial.Vehicles![0].VehicleId);
    Assert.Equal(GtfsFetchStatus.NotModified, revalidated.Status);
    Assert.Equal(2, handler.Requests.Count);
    Assert.Empty(handler.Requests[0].EntityTags);
    Assert.Equal(["\"feed-v1\""], handler.Requests[1].EntityTags);
    Assert.Equal(lastModified, handler.Requests[1].IfModifiedSince);
  }

  [Fact]
  public async Task GetBusPositionsAsync_UsesFreshnessLifetimeWithoutAnotherDownload()
  {
    var handler = new RecordingHandler(_ => FeedResponse(
      "vehicle-1",
      cacheControl: new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) }));
    var service = new GtfsService(new HttpClient(handler));

    var initial = await service.GetBusPositionsAsync(FeedUrl, allowNotModified: false);
    var cached = await service.GetBusPositionsAsync(FeedUrl, allowNotModified: true);

    Assert.Equal(GtfsFetchStatus.Updated, initial.Status);
    Assert.Equal(GtfsFetchStatus.NotModified, cached.Status);
    Assert.Single(handler.Requests);
  }

  [Fact]
  public async Task GetBusPositionsAsync_ForcesFullResponseWhenCallerHasNoSnapshot()
  {
    var cacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) };
    var handler = new RecordingHandler(
      _ => FeedResponse("vehicle-1", entityTag: "\"feed-v1\"", cacheControl: cacheControl),
      _ => FeedResponse("vehicle-2", entityTag: "\"feed-v2\"", cacheControl: cacheControl));
    var service = new GtfsService(new HttpClient(handler));

    await service.GetBusPositionsAsync(FeedUrl, allowNotModified: false);
    var forced = await service.GetBusPositionsAsync(FeedUrl, allowNotModified: false);

    Assert.Equal(GtfsFetchStatus.Updated, forced.Status);
    Assert.Equal("vehicle-2", forced.Vehicles![0].VehicleId);
    Assert.Equal(2, handler.Requests.Count);
    Assert.Empty(handler.Requests[1].EntityTags);
    Assert.Null(handler.Requests[1].IfModifiedSince);
  }

  [Fact]
  public async Task GetBusPositionsAsync_PropagatesCallerCancellation()
  {
    var handler = new RecordingHandler(_ => FeedResponse("vehicle-1"));
    var service = new GtfsService(new HttpClient(handler));
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      service.GetBusPositionsAsync(FeedUrl, allowNotModified: false, cancellation.Token));
  }

  [Fact]
  public async Task GetBusPositionsAsync_RejectsDeclaredOversizedFeedBeforeReadingBody()
  {
    var oversizedContent = new TrackingContent(64L * 1024 * 1024 + 1);
    var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = oversizedContent
    });
    var service = new GtfsService(new HttpClient(handler));

    var result = await service.GetBusPositionsAsync(FeedUrl, allowNotModified: false);

    Assert.Equal(GtfsFetchStatus.Failed, result.Status);
    Assert.False(oversizedContent.WasRead);
  }

  private static HttpResponseMessage FeedResponse(
    string vehicleId,
    string? entityTag = null,
    DateTimeOffset? lastModified = null,
    CacheControlHeaderValue? cacheControl = null)
  {
    var feed = new FeedMessage
    {
      Header = new FeedHeader
      {
        GtfsRealtimeVersion = "2.0",
        Timestamp = 1_788_000_000UL
      }
    };
    feed.Entity.Add(new FeedEntity
    {
      Id = "entity-1",
      Vehicle = new VehiclePosition
      {
        Vehicle = new VehicleDescriptor { Id = vehicleId },
        Position = new Position { Latitude = 3.139f, Longitude = 101.687f }
      }
    });

    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new ByteArrayContent(feed.ToByteArray())
    };
    response.Headers.ETag = entityTag is null ? null : EntityTagHeaderValue.Parse(entityTag);
    response.Headers.CacheControl = cacheControl;
    response.Content.Headers.LastModified = lastModified;
    return response;
  }

  private sealed record RequestSnapshot(
    IReadOnlyList<string> EntityTags,
    DateTimeOffset? IfModifiedSince);

  private sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    : HttpMessageHandler
  {
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

    public List<RequestSnapshot> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(new RequestSnapshot(
        request.Headers.IfNoneMatch.Select(tag => tag.ToString()).ToArray(),
        request.Headers.IfModifiedSince));

      if (!_responses.TryDequeue(out var response))
      {
        throw new InvalidOperationException("The test made an unexpected HTTP request.");
      }

      return Task.FromResult(response(request));
    }
  }

  private sealed class TrackingContent(long declaredLength) : HttpContent
  {
    public bool WasRead { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
      WasRead = true;
      return Task.CompletedTask;
    }

    protected override bool TryComputeLength(out long length)
    {
      length = declaredLength;
      return true;
    }
  }
}
