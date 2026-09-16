using Google.Protobuf;
using MyTransportAppWASM.Gtfs;
using MyTransportAppWASM.Models;
using TransitRealtime;

namespace MyTransportAppWASM.Tests;

public sealed class GtfsFeedParserTests
{
  [Fact]
  public void ParseVehicles_MatchesGeneratedParserProjection()
  {
    var source = BuildFeed(200);
    var payload = source.ToByteArray();
    var paddedPayload = new byte[payload.Length + 11];
    payload.CopyTo(paddedPayload, 7);

    var actual = Parse(paddedPayload.AsMemory(7, payload.Length));
    var expected = source.Entity
      .Where(entity => entity.Vehicle?.Position is not null)
      .Select(entity => new BusLocation
      {
        RouteId = entity.Vehicle.Trip?.RouteId,
        VehicleId = entity.Vehicle.Vehicle?.Id,
        Lat = entity.Vehicle.Position.Latitude,
        Lng = entity.Vehicle.Position.Longitude,
        Bearing = entity.Vehicle.Position.Bearing,
        Speed = entity.Vehicle.Position.Speed,
        Timestamp = entity.Vehicle.Timestamp
      })
      .ToList();

    Assert.Equal(expected.Count, actual.Count);
    for (var index = 0; index < expected.Count; index++)
    {
      Assert.Equal(expected[index], actual[index]);
    }
  }

  [Fact]
  public void ParseVehicles_SkipsUnknownTopLevelField()
  {
    var payload = BuildFeed(1).ToByteArray();
    var extended = new byte[payload.Length + 3];
    payload.CopyTo(extended, 0);
    // field 500, varint wire type, value 123
    extended[^3] = 0xA0;
    extended[^2] = 0x1F;
    extended[^1] = 0x7B;

    var parsed = Parse(extended);

    Assert.Single(parsed);
    Assert.Equal("vehicle-0", parsed[0].VehicleId);
  }

  [Fact]
  public void ParseVehicles_RejectsTruncatedNestedMessage()
  {
    var payload = BuildFeed(1).ToByteArray();

    Assert.Throws<GtfsFeedParsingException>(() => Parse(payload.AsMemory(0, payload.Length - 1)));
  }

  [Fact]
  public void ParseVehicles_ObservesPreCancelledToken()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    Assert.ThrowsAny<OperationCanceledException>(() =>
      Parse(BuildFeed(1).ToByteArray(), cancellation.Token));
  }

  private static List<BusLocation> Parse(
    ReadOnlyMemory<byte> payload,
    CancellationToken cancellationToken = default)
  {
    var vehicles = new List<BusLocation>();
    GtfsFeedParser.ParseVehicles(
      payload,
      count => { vehicles.EnsureCapacity(count); },
      (routeId, vehicleId, latitude, longitude, bearing, speed, timestamp) =>
        vehicles.Add(new BusLocation
        {
          RouteId = routeId,
          VehicleId = vehicleId,
          Lat = latitude,
          Lng = longitude,
          Bearing = bearing,
          Speed = speed,
          Timestamp = timestamp
        }),
      cancellationToken);
    return vehicles;
  }

  private static FeedMessage BuildFeed(int entityCount)
  {
    var feed = new FeedMessage
    {
      Header = new FeedHeader
      {
        GtfsRealtimeVersion = "2.0",
        Timestamp = 1_788_000_000UL
      }
    };

    for (var index = 0; index < entityCount; index++)
    {
      var entity = new FeedEntity { Id = $"entity-{index}" };
      if (index % 9 != 8)
      {
        var trip = new TripDescriptor
        {
          TripId = $"trip-{index}",
          StartTime = "08:00:00"
        };
        if (index % 4 != 3)
        {
          trip.RouteId = $"T{index % 40}";
        }

        var descriptor = new VehicleDescriptor
        {
          Label = $"Bus {index}",
          LicensePlate = $"TEST-{index}"
        };
        if (index % 5 != 4)
        {
          descriptor.Id = $"vehicle-{index}";
        }

        entity.Vehicle = new VehiclePosition
        {
          Trip = trip,
          Vehicle = descriptor,
          Position = new Position
          {
            Latitude = 3.1f + index * 0.001f,
            Longitude = 101.65f + index * 0.001f,
            Bearing = index % 360,
            Odometer = index * 100,
            Speed = 8.5f + index * 0.01f
          },
          Timestamp = 1_788_000_000UL + (ulong)index,
          StopId = $"stop-{index}"
        };
      }

      feed.Entity.Add(entity);
    }

    return feed;
  }
}
