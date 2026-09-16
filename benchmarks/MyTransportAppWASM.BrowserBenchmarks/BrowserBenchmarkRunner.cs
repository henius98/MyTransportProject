using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MyTransportAppWASM.Gtfs;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.BrowserBenchmarks;

public sealed record BrowserBenchmarkResult(
  string Name,
  double MedianMicroseconds,
  double P95Microseconds,
  long AllocatedBytesPerOperation);

public static class BrowserBenchmarkRunner
{
  private const int WarmupIterations = 12;

  public static IReadOnlyList<BrowserBenchmarkResult> Run()
  {
    var typicalRoutes = CreateRoutes(routeCount: 3, stepsPerRoute: 3);
    var typicalVehicles = CreateVehicles(vehicleCount: 1_000, distinctRoutes: 40);
    var stressRoutes = CreateRoutes(routeCount: 10, stepsPerRoute: 6);
    var stressVehicles = CreateVehicles(vehicleCount: 5_000, distinctRoutes: 80);
    var routingService = new LiveRoutingService();
    using var typicalWeather = CreateWeatherDocument(periodCount: 14);
    using var stressWeather = CreateWeatherDocument(periodCount: 500);
    var fallback = Array.Empty<WeatherTimeSlice>();
    var protobufPayload = CreateFeedPayload(vehicleCount: 2_000);

    return
    [
      Measure(
        "LiveRouting/typical-3x3x1000",
        iterations: 120,
        () => routingService.EnrichRoutesAsync(typicalRoutes, typicalVehicles).GetAwaiter().GetResult().Count),
      Measure(
        "LiveRouting/stress-10x6x5000",
        iterations: 30,
        () => routingService.EnrichRoutesAsync(stressRoutes, stressVehicles).GetAwaiter().GetResult().Count),
      Measure(
        "WeatherShaper/generic-14",
        iterations: 300,
        () => WeatherShaper.ShapeSlices(typicalWeather, fallback, "Baseline").Count),
      Measure(
        "WeatherShaper/generic-500",
        iterations: 30,
        () => WeatherShaper.ShapeSlices(stressWeather, fallback, "Outlook").Count),
      Measure(
        "GtfsFeedParser/project-2000-vehicles",
        iterations: 100,
        () => ParseProjectedVehicles(protobufPayload))
    ];
  }

  private static BrowserBenchmarkResult Measure(string name, int iterations, Func<int> operation)
  {
    var checksum = 0;
    for (var index = 0; index < WarmupIterations; index++)
    {
      checksum ^= operation();
    }

    GC.Collect();
    var elapsedTicks = new long[iterations];
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var index = 0; index < iterations; index++)
    {
      var started = Stopwatch.GetTimestamp();
      checksum ^= operation();
      elapsedTicks[index] = Stopwatch.GetTimestamp() - started;
    }

    GC.KeepAlive(checksum);
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Array.Sort(elapsedTicks);
    var medianMicroseconds = elapsedTicks[iterations / 2] * 1_000_000d / Stopwatch.Frequency;
    var p95Microseconds = elapsedTicks[(int)Math.Ceiling(iterations * 0.95) - 1] * 1_000_000d / Stopwatch.Frequency;
    return new BrowserBenchmarkResult(
      name,
      medianMicroseconds,
      p95Microseconds,
      allocatedBytes / iterations);
  }

  private static int ParseProjectedVehicles(byte[] payload)
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

  private static List<GoogleRoute> CreateRoutes(int routeCount, int stepsPerRoute)
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
          LineShortName = $"T-{stepIndex + 1}",
          DepartureLat = 3.139 + routeIndex * 0.002 + stepIndex * 0.001,
          DepartureLng = 101.687 + routeIndex * 0.002 + stepIndex * 0.001
        });
      }
      routes.Add(route);
    }
    return routes;
  }

  private static List<BusLocation> CreateVehicles(int vehicleCount, int distinctRoutes)
  {
    var vehicles = new List<BusLocation>(vehicleCount);
    for (var index = 0; index < vehicleCount; index++)
    {
      vehicles.Add(new BusLocation
      {
        VehicleId = $"vehicle-{index}",
        RouteId = $"t {(index % distinctRoutes) + 1}",
        Lat = 3.10f + (index % 100) * 0.001f,
        Lng = 101.65f + (index % 120) * 0.001f,
        Speed = 8.5f,
        Timestamp = 1_788_000_000UL + (ulong)index
      });
    }
    return vehicles;
  }

  private static JsonDocument CreateWeatherDocument(int periodCount)
  {
    var json = new StringBuilder(periodCount * 220);
    json.Append('[');
    for (var index = 0; index < periodCount; index++)
    {
      if (index != 0) json.Append(',');
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

  private static byte[] CreateFeedPayload(int vehicleCount)
  {
    using var feed = new MemoryStream(vehicleCount * 100);
    using (var header = new MemoryStream())
    {
      WriteString(header, fieldNumber: 1, "2.0");
      WriteVarintField(header, fieldNumber: 3, 1_788_000_000UL);
      WriteMessage(feed, fieldNumber: 1, header);
    }

    for (var index = 0; index < vehicleCount; index++)
    {
      using var entity = new MemoryStream(128);
      WriteString(entity, fieldNumber: 1, index.ToString(CultureInfo.InvariantCulture));
      using (var vehicle = new MemoryStream(96))
      {
        using (var trip = new MemoryStream(16))
        {
          WriteString(trip, fieldNumber: 5, $"T{index % 40}");
          WriteMessage(vehicle, fieldNumber: 1, trip);
        }
        using (var position = new MemoryStream(24))
        {
          WriteFloat(position, fieldNumber: 1, 3.10f + (index % 100) * 0.001f);
          WriteFloat(position, fieldNumber: 2, 101.65f + (index % 120) * 0.001f);
          WriteFloat(position, fieldNumber: 3, index % 360);
          WriteFloat(position, fieldNumber: 5, 8.5f);
          WriteMessage(vehicle, fieldNumber: 2, position);
        }
        WriteVarintField(vehicle, fieldNumber: 5, 1_788_000_000UL + (ulong)index);
        using (var descriptor = new MemoryStream(24))
        {
          WriteString(descriptor, fieldNumber: 1, $"vehicle-{index}");
          WriteMessage(vehicle, fieldNumber: 8, descriptor);
        }
        WriteMessage(entity, fieldNumber: 4, vehicle);
      }
      WriteMessage(feed, fieldNumber: 2, entity);
    }
    return feed.ToArray();
  }

  private static void WriteMessage(Stream destination, int fieldNumber, MemoryStream message)
  {
    WriteTag(destination, fieldNumber, wireType: 2);
    WriteVarint(destination, (ulong)message.Length);
    message.Position = 0;
    message.CopyTo(destination);
  }

  private static void WriteString(Stream destination, int fieldNumber, string value)
  {
    var bytes = Encoding.UTF8.GetBytes(value);
    WriteTag(destination, fieldNumber, wireType: 2);
    WriteVarint(destination, (ulong)bytes.Length);
    destination.Write(bytes);
  }

  private static void WriteFloat(Stream destination, int fieldNumber, float value)
  {
    WriteTag(destination, fieldNumber, wireType: 5);
    Span<byte> bytes = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
    destination.Write(bytes);
  }

  private static void WriteVarintField(Stream destination, int fieldNumber, ulong value)
  {
    WriteTag(destination, fieldNumber, wireType: 0);
    WriteVarint(destination, value);
  }

  private static void WriteTag(Stream destination, int fieldNumber, int wireType) =>
    WriteVarint(destination, (ulong)((fieldNumber << 3) | wireType));

  private static void WriteVarint(Stream destination, ulong value)
  {
    while (value >= 0x80)
    {
      destination.WriteByte((byte)((value & 0x7f) | 0x80));
      value >>= 7;
    }
    destination.WriteByte((byte)value);
  }
}
