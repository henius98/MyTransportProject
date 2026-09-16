using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using MyTransportAppWASM.Gtfs;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Utils;
using TransitRealtime;

const int warmupIterations = 8;

var typicalRoutes = CreateRoutes(routeCount: 3, stepsPerRoute: 3);
var typicalVehicles = CreateVehicles(vehicleCount: 1_000, distinctRoutes: 40);
var stressRoutes = CreateRoutes(routeCount: 10, stepsPerRoute: 6);
var stressVehicles = CreateVehicles(vehicleCount: 5_000, distinctRoutes: 80);
var comparableStressRoutes = CreateRoutes(
    routeCount: 10,
    stepsPerRoute: 6,
    useFixedWidthRouteNames: true);
var comparableStressVehicles = CreateVehicles(
    vehicleCount: 5_000,
    distinctRoutes: 80,
    useFixedWidthRouteNames: true);
var routingService = new LiveRoutingService();

using var typicalWeather = CreateWeatherDocument(periodCount: 14);
using var stressWeather = CreateWeatherDocument(periodCount: 500);
var fallback = Array.Empty<WeatherTimeSlice>();
var protobufPayload = CreateFeedPayload(vehicleCount: 2_000);

Run(
    "LiveRouting/typical-3x3x1000",
    iterations: 120,
    () => routingService.EnrichRoutesAsync(typicalRoutes, typicalVehicles).GetAwaiter().GetResult().Count);

Run(
    "LiveRouting/stress-10x6x5000",
    iterations: 30,
    () => routingService.EnrichRoutesAsync(stressRoutes, stressVehicles).GetAwaiter().GetResult().Count);

Run(
    "LiveRouting/legacy-compatible-stress",
    iterations: 20,
    () => EnrichRoutesLegacy(comparableStressRoutes, comparableStressVehicles).Count);

Run(
    "LiveRouting/indexed-compatible-stress",
    iterations: 20,
    () => routingService.EnrichRoutesAsync(comparableStressRoutes, comparableStressVehicles).GetAwaiter().GetResult().Count);

Run(
    "WeatherShaper/generic-14",
    iterations: 300,
    () => WeatherShaper.ShapeSlices(typicalWeather, fallback, "Baseline").Count);

Run(
    "WeatherShaper/generic-500",
    iterations: 30,
    () => WeatherShaper.ShapeSlices(stressWeather, fallback, "Outlook").Count);

Run(
    "ProtocolBuffer/direct-2000-vehicles",
    iterations: 100,
    () => FeedMessage.Parser.ParseFrom(protobufPayload).Entity.Count);

Run(
    "ProtocolBuffer/generated-project-2000",
    iterations: 100,
    () => ParseGeneratedProjectedVehicles(protobufPayload));

Run(
    "ProtocolBuffer/project-2000-vehicles",
    iterations: 100,
    () => ParseProjectedVehicles(protobufPayload));

void Run(string name, int iterations, Func<int> operation)
{
  var checksum = 0;
  for (var i = 0; i < warmupIterations; i++)
  {
    checksum ^= operation();
  }

  GC.Collect();
  GC.WaitForPendingFinalizers();
  GC.Collect();

  var elapsedTicks = new long[iterations];
  var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

  for (var i = 0; i < iterations; i++)
  {
    var started = Stopwatch.GetTimestamp();
    checksum ^= operation();
    elapsedTicks[i] = Stopwatch.GetTimestamp() - started;
  }

  var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
  Array.Sort(elapsedTicks);

  var medianNanoseconds = elapsedTicks[iterations / 2] * 1_000_000_000d / Stopwatch.Frequency;
  var p95Nanoseconds = elapsedTicks[(int)Math.Ceiling(iterations * 0.95) - 1] * 1_000_000_000d / Stopwatch.Frequency;

  Console.WriteLine(
      $"{name,-42} median_us={medianNanoseconds / 1_000d,10:F2} " +
      $"p95_us={p95Nanoseconds / 1_000d,10:F2} alloc_bytes/op={allocatedBytes / iterations,12} checksum={checksum}");
}

static List<GoogleRoute> CreateRoutes(
    int routeCount,
    int stepsPerRoute,
    bool useFixedWidthRouteNames = false)
{
  var routes = new List<GoogleRoute>(routeCount);

  for (var routeIndex = 0; routeIndex < routeCount; routeIndex++)
  {
    var route = new GoogleRoute
    {
      RouteId = routeIndex,
      DurationSeconds = 1_800 + routeIndex * 60,
      DistanceMeters = 12_000 + routeIndex * 250
    };

    for (var stepIndex = 0; stepIndex < stepsPerRoute; stepIndex++)
    {
      route.TransitSteps.Add(new TransitStep
      {
        LineShortName = useFixedWidthRouteNames
              ? $"R-{stepIndex + 1:D3}"
              : $"T-{stepIndex + 1}",
        DepartureLat = 3.139 + routeIndex * 0.002 + stepIndex * 0.001,
        DepartureLng = 101.687 + routeIndex * 0.002 + stepIndex * 0.001
      });
    }

    routes.Add(route);
  }

  return routes;
}

static List<BusLocation> CreateVehicles(
    int vehicleCount,
    int distinctRoutes,
    bool useFixedWidthRouteNames = false)
{
  var vehicles = new List<BusLocation>(vehicleCount);

  for (var index = 0; index < vehicleCount; index++)
  {
    vehicles.Add(new BusLocation
    {
      VehicleId = $"vehicle-{index}",
      RouteId = useFixedWidthRouteNames
            ? $"R-{(index % distinctRoutes) + 1:D3}"
            : $"t {(index % distinctRoutes) + 1}",
      Lat = 3.10f + (index % 100) * 0.001f,
      Lng = 101.65f + (index % 120) * 0.001f,
      Speed = 8.5f,
      Timestamp = 1_788_000_000UL + (ulong)index
    });
  }

  return vehicles;
}

static JsonDocument CreateWeatherDocument(int periodCount)
{
  var json = new StringBuilder(periodCount * 220);
  json.Append('[');

  for (var index = 0; index < periodCount; index++)
  {
    if (index != 0)
    {
      json.Append(',');
    }

    json.Append("{\"location\":{\"name\":\"Kuala Lumpur\"},\"forecast\":{")
        .Append("\"date\":\"2026-08-").Append((index % 28) + 1).Append("T00:00:00Z\",")
        .Append("\"temperature_2m_max\":32.5,\"temperature_2m_min\":24.0,")
        .Append("\"precipitation\":4.2,\"precipitation_probability\":75,")
        .Append("\"wind_speed\":\"12 km/h\",\"morning_forecast\":\"Partly cloudy\",")
        .Append("\"afternoon_forecast\":\"Thunderstorms in a few places\",")
        .Append("\"night_forecast\":\"Rain\"}}");
  }

  json.Append(']');
  return JsonDocument.Parse(json.ToString());
}

static byte[] CreateFeedPayload(int vehicleCount)
{
  var feed = new FeedMessage
  {
    Header = new FeedHeader
    {
      GtfsRealtimeVersion = "2.0",
      Timestamp = 1_788_000_000UL
    }
  };

  for (var index = 0; index < vehicleCount; index++)
  {
    feed.Entity.Add(new FeedEntity
    {
      Id = index.ToString(),
      Vehicle = new VehiclePosition
      {
        Trip = new TripDescriptor { RouteId = $"T{index % 40}" },
        Vehicle = new VehicleDescriptor { Id = $"vehicle-{index}" },
        Position = new Position
        {
          Latitude = 3.10f + (index % 100) * 0.001f,
          Longitude = 101.65f + (index % 120) * 0.001f,
          Speed = 8.5f
        },
        Timestamp = 1_788_000_000UL + (ulong)index
      }
    });
  }

  return feed.ToByteArray();
}

static int ParseProjectedVehicles(byte[] payload)
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
          }));
  return vehicles.Count;
}

static int ParseGeneratedProjectedVehicles(byte[] payload)
{
  var feed = FeedMessage.Parser.ParseFrom(payload);
  var vehicles = feed.Entity
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
  return vehicles.Count;
}

static List<EnrichedRoute> EnrichRoutesLegacy(
    List<GoogleRoute> googleRoutes,
    List<BusLocation> liveVehicles)
{
  var result = new List<EnrichedRoute>();

  foreach (var route in googleRoutes)
  {
    var enriched = new EnrichedRoute
    {
      OriginalRoute = route,
      LiveTotalDurationSeconds = route.DurationSeconds,
      HasLiveBus = false
    };

    foreach (var step in route.TransitSteps)
    {
      var matchingBuses = liveVehicles.Where(vehicle =>
          !string.IsNullOrEmpty(vehicle.RouteId) &&
          (vehicle.RouteId.Contains(step.LineShortName, StringComparison.OrdinalIgnoreCase) ||
           step.LineShortName.Contains(vehicle.RouteId, StringComparison.OrdinalIgnoreCase)))
          .ToList();

      if (matchingBuses.Count == 0)
      {
        continue;
      }

      var closestBus = matchingBuses
          .Select(bus => new
          {
            Bus = bus,
            Distance = CalculateDistanceLegacy(
                  bus.Lat,
                  bus.Lng,
                  step.DepartureLat,
                  step.DepartureLng)
          })
          .OrderBy(candidate => candidate.Distance)
          .First();
      var estimatedSeconds = (int)(closestBus.Distance / 5.5);

      enriched.NearestBuses.Add(new LiveBusInfo
      {
        VehicleId = closestBus.Bus.VehicleId ?? string.Empty,
        RouteId = closestBus.Bus.RouteId ?? string.Empty,
        Lat = closestBus.Bus.Lat,
        Lng = closestBus.Bus.Lng,
        DistanceToStopMeters = closestBus.Distance,
        EstimatedArrivalSeconds = estimatedSeconds
      });
      enriched.HasLiveBus = true;
      enriched.LiveTotalDurationSeconds += estimatedSeconds;
    }

    result.Add(enriched);
  }

  return result
      .OrderByDescending(route => route.HasLiveBus)
      .ThenBy(route => route.LiveTotalDurationSeconds)
      .ToList();
}

static double CalculateDistanceLegacy(
    double latitude1,
    double longitude1,
    double latitude2,
    double longitude2)
{
  const double earthRadiusMeters = 6_371_000;
  var latitudeDelta = ToRadiansLegacy(latitude2 - latitude1);
  var longitudeDelta = ToRadiansLegacy(longitude2 - longitude1);
  var haversine = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
                  Math.Cos(ToRadiansLegacy(latitude1)) * Math.Cos(ToRadiansLegacy(latitude2)) *
                  Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
  return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
}

static double ToRadiansLegacy(double degrees) => degrees * (Math.PI / 180);
