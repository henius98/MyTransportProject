namespace MyTransportAppWASM.Tests;

public sealed class ScheduledRailPositionEstimatorTests
{
  private static readonly TransportProvider RapidRail = new(
    "Rapid Rail KL",
    "",
    3.139,
    101.6869,
    60.0,
    "Rail",
    new TransportStaticData("https://worker.example/rapid-rail-kl", "/data/routes", "/map", "/departures", "/data/frequencies"));

  [Fact]
  public void EstimateVehicles_FollowsCurvedShapeAndComputesBearingAndSpeed()
  {
    // L-shaped track: (3.00, 101.00) -> (3.00, 101.02) -> (3.02, 101.02)
    // Halfway in time should sit near the corner (3.00, 101.02), NOT on the straight diagonal (3.01, 101.01).
    const string mapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": {
              "type": "LineString",
              "coordinates": [[101.00, 3.00], [101.02, 3.00], [101.02, 3.02]]
            },
            "properties": {
              "kind": "route",
              "route_id": "KJ",
              "shape_id": "shp_KJ_0",
              "trip_ids": ["KJL_MonFri_0"],
              "route_short_name": "KJL"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.00, 3.00] },
            "properties": {
              "kind": "stop", "route_id": "KJ", "stop_id": "S1",
              "trip_ids": ["KJL_MonFri_0"],
              "arrival_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.02, 3.02] },
            "properties": {
              "kind": "stop", "route_id": "KJ", "stop_id": "S2",
              "trip_ids": ["KJL_MonFri_0"],
              "arrival_times": "06:03:20"
            }
          }
        ]
      }
      """;

    IReadOnlyList<StaticFrequency> frequencies =
    [
      new("KJL_MonFri_0", "6:00:00", "7:00:00", 600)
    ];

    var network = ScheduledRailPositionEstimator.CompileForServiceDay(mapGeoJson, frequencies);

    // Wednesday 2026-06-03 at 06:01:40 MYT (100s after 06:00:00 departure = 50% of 200s segment)
    var now = new DateTimeOffset(2026, 6, 3, 6, 1, 40, MalaysiaTime.Offset);
    var vehicles = ScheduledRailPositionEstimator.EstimateVehicles(network, RapidRail, now);

    var train = Assert.Single(vehicles);
    Assert.True(train.IsEstimated);
    Assert.Equal("Rapid Rail KL", train.ProviderName);
    Assert.Equal("LRT", train.TransportType);
    Assert.Equal("KJ", train.RouteId);
    Assert.Equal("KJL", train.RouteShortName);
    Assert.Equal("KJL-0-0600", train.VehicleId);
    Assert.InRange(train.Lat, 2.999f, 3.002f);
    Assert.InRange(train.Lng, 101.019f, 101.021f);
    Assert.True(train.Speed > 15f);
  }

  [Fact]
  public void EstimateVehicles_RespectsOffsetStopStartAlongShapeAndStopAtStation()
  {
    // Track starts at 101.00, while S1 is halfway along at 101.01 (like KGL starting past old terminus).
    // Stop features omit trip_ids to also verify symmetric fallback alignment with sorted route trip_ids.
    const string mapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": {
              "type": "LineString",
              "coordinates": [[101.00, 3.10], [101.01, 3.10], [101.02, 3.10]]
            },
            "properties": {
              "kind": "route",
              "route_id": "KGL",
              "shape_id": "shp_MRT_0",
              "trip_ids": ["KGL_MonFri_0"],
              "route_short_name": "KGL"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.01, 3.10] },
            "properties": {
              "kind": "stop", "route_id": "KGL", "stop_id": "S1",
              "arrival_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.02, 3.10] },
            "properties": {
              "kind": "stop", "route_id": "KGL", "stop_id": "S2",
              "arrival_times": "06:02:00"
            }
          }
        ]
      }
      """;

    IReadOnlyList<StaticFrequency> frequencies =
    [
      new("KGL_MonFri_0", "8:00:00", "9:00:00", 600)
    ];

    var network = ScheduledRailPositionEstimator.CompileForServiceDay(mapGeoJson, frequencies);

    // At 07:59:50 MYT: train has not started yet; at 08:00:00 MYT: train is at S1
    Assert.Empty(ScheduledRailPositionEstimator.EstimateVehicles(
      network, RapidRail, new DateTimeOffset(2026, 6, 3, 7, 59, 50, MalaysiaTime.Offset)));

    var atOrigin = Assert.Single(ScheduledRailPositionEstimator.EstimateVehicles(
      network, RapidRail, new DateTimeOffset(2026, 6, 3, 8, 0, 0, MalaysiaTime.Offset)));
    Assert.Equal("MRT", atOrigin.TransportType);
    Assert.Equal(0f, atOrigin.Speed);
    Assert.InRange(atOrigin.Lng, 101.0099f, 101.0101f);
    Assert.InRange(atOrigin.Bearing, 88f, 92f);

    var atTerminus = Assert.Single(ScheduledRailPositionEstimator.EstimateVehicles(
      network, RapidRail, new DateTimeOffset(2026, 6, 3, 8, 2, 0, MalaysiaTime.Offset)));
    Assert.Equal(0f, atTerminus.Speed);
    Assert.InRange(atTerminus.Lng, 101.0199f, 101.0201f);
  }

  [Fact]
  public void EstimateVehicles_HandlesAsymmetricExpressTripsUsingPerStopTripIds()
  {
    // Route AG has two trips: regular stops at S1->S2->S3; express skips S2 (S1->S3).
    const string mapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": { "type": "LineString", "coordinates": [[101.70, 3.10], [101.70, 3.12], [101.70, 3.14]] },
            "properties": {
              "kind": "route", "route_id": "AG", "shape_id": "shp_AG_0",
              "trip_ids": ["AGL_Exp_0", "AGL_Reg_0"],
              "route_short_name": "AGL"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.10] },
            "properties": {
              "kind": "stop", "route_id": "AG", "stop_id": "S1",
              "trip_ids": ["AGL_Exp_0", "AGL_Reg_0"],
              "arrival_times": "06:00:00, 06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.12] },
            "properties": {
              "kind": "stop", "route_id": "AG", "stop_id": "S2",
              "trip_ids": ["AGL_Reg_0"],
              "arrival_times": "06:05:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.14] },
            "properties": {
              "kind": "stop", "route_id": "AG", "stop_id": "S3",
              "trip_ids": ["AGL_Exp_0", "AGL_Reg_0"],
              "arrival_times": "06:05:00, 06:10:00"
            }
          }
        ]
      }
      """;

    IReadOnlyList<StaticFrequency> frequencies =
    [
      new("AGL_Exp_0", "8:00:00", "8:01:00", 600),
      new("AGL_Reg_0", "8:00:00", "8:01:00", 600)
    ];

    var network = ScheduledRailPositionEstimator.CompileForServiceDay(mapGeoJson, frequencies);
    // At 08:02:30 (150s): Express is 50% of S1->S3 (lat ~3.12), Regular is 50% of S1->S2 (lat ~3.11)
    var vehicles = ScheduledRailPositionEstimator.EstimateVehicles(
      network, RapidRail, new DateTimeOffset(2026, 6, 3, 8, 2, 30, MalaysiaTime.Offset));

    Assert.Equal(2, vehicles.Count);
    var lats = vehicles.Select(v => v.Lat).OrderBy(lat => lat).ToArray();
    Assert.InRange(lats[0], 3.109f, 3.111f);
    Assert.InRange(lats[1], 3.119f, 3.121f);
  }

  [Fact]
  public void EstimateVehicles_DeduplicatesHalfOpenWindowsAndStopsAfterMidnight()
  {
    const string fridayMapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.68, 3.13] },
            "properties": {
              "kind": "stop", "route_id": "BRT", "stop_id": "BRT1",
              "trip_ids": ["BRT_MonFri_0"],
              "arrival_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.17] },
            "properties": {
              "kind": "stop", "route_id": "BRT", "stop_id": "BRT2",
              "trip_ids": ["BRT_MonFri_0"],
              "arrival_times": "06:16:00"
            }
          }
        ]
      }
      """;

    IReadOnlyList<StaticFrequency> frequencies =
    [
      new("BRT_MonFri_0", "6:00:00", "7:00:00", 480),
      new("BRT_MonFri_0", "7:00:00", "9:00:00", 240),
      new("BRT_MonFri_0", "9:00:00", "17:00:00", 480),
      new("BRT_MonFri_0", "17:00:00", "19:00:00", 240),
      new("BRT_MonFri_0", "19:00:00", "24:00:00", 480)
    ];

    var fridayNetwork = ScheduledRailPositionEstimator.CompileForServiceDay(fridayMapGeoJson, frequencies);

    // At 09:00:00: adjacent windows [07:00, 09:00) and [09:00, 17:00) do not duplicate 09:00
    var boundaryVehicles = ScheduledRailPositionEstimator.EstimateVehicles(
      fridayNetwork, RapidRail, new DateTimeOffset(2026, 6, 5, 9, 0, 0, MalaysiaTime.Offset));
    Assert.Equal(5, boundaryVehicles.Count);
    Assert.Single(boundaryVehicles, v => v.VehicleId == "BRT-0-0900");

    // At 23:58:00 (before midnight): late evening trips are active
    var beforeMidnight = ScheduledRailPositionEstimator.EstimateVehicles(
      fridayNetwork, RapidRail, new DateTimeOffset(2026, 6, 5, 23, 58, 0, MalaysiaTime.Offset));
    Assert.NotEmpty(beforeMidnight);

    // After 12:00 AM (00:00:00 and 00:10:59): no estimated rail vehicles are shown
    Assert.Empty(ScheduledRailPositionEstimator.EstimateVehicles(
      fridayNetwork, RapidRail, new DateTimeOffset(2026, 6, 6, 0, 0, 0, MalaysiaTime.Offset)));
    Assert.Empty(ScheduledRailPositionEstimator.EstimateVehicles(
      fridayNetwork, RapidRail, new DateTimeOffset(2026, 6, 6, 0, 10, 59, MalaysiaTime.Offset)));
  }
}
