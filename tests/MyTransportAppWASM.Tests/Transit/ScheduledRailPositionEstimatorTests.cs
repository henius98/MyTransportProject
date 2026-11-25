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
              "arrival_times": "05:59:42", "departure_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.02, 3.02] },
            "properties": {
              "kind": "stop", "route_id": "KJ", "stop_id": "S2",
              "trip_ids": ["KJL_MonFri_0"],
              "arrival_times": "06:03:20", "departure_times": "06:03:20"
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
    var vehicles = ScheduledRailPositionEstimator.EstimateVehicles(network, null, RapidRail, now);

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
  public void EstimateVehicles_RespectsOffsetStopStartAlongShapeAndDwellAtStation()
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
              "arrival_times": "05:59:42", "departure_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.02, 3.10] },
            "properties": {
              "kind": "stop", "route_id": "KGL", "stop_id": "S2",
              "arrival_times": "06:01:42", "departure_times": "06:02:00"
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

    // At 07:59:50 MYT: train is dwelling at S1 (10s before departure)
    var dwelling = Assert.Single(ScheduledRailPositionEstimator.EstimateVehicles(
      network, null, RapidRail, new DateTimeOffset(2026, 6, 3, 7, 59, 50, MalaysiaTime.Offset)));
    Assert.Equal("MRT", dwelling.TransportType);
    Assert.Equal(0f, dwelling.Speed);
    Assert.InRange(dwelling.Lng, 101.0099f, 101.0101f);
    Assert.InRange(dwelling.Bearing, 88f, 92f);
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
              "arrival_times": "06:00:00, 06:00:00",
              "departure_times": "06:00:00, 06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.12] },
            "properties": {
              "kind": "stop", "route_id": "AG", "stop_id": "S2",
              "trip_ids": ["AGL_Reg_0"],
              "arrival_times": "06:05:00",
              "departure_times": "06:05:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.14] },
            "properties": {
              "kind": "stop", "route_id": "AG", "stop_id": "S3",
              "trip_ids": ["AGL_Exp_0", "AGL_Reg_0"],
              "arrival_times": "06:05:00, 06:10:00",
              "departure_times": "06:05:00, 06:10:00"
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
      network, null, RapidRail, new DateTimeOffset(2026, 6, 3, 8, 2, 30, MalaysiaTime.Offset));

    Assert.Equal(2, vehicles.Count);
    var lats = vehicles.Select(v => v.Lat).OrderBy(lat => lat).ToArray();
    Assert.InRange(lats[0], 3.109f, 3.111f);
    Assert.InRange(lats[1], 3.119f, 3.121f);
  }

  [Fact]
  public void EstimateVehicles_DeduplicatesHalfOpenWindowsAndSupportsPostMidnightRollover()
  {
    const string fridayMapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.68, 3.13] },
            "properties": {
              "kind": "stop", "route_id": "MR", "stop_id": "MR1",
              "trip_ids": ["MRL_MonFri_0"],
              "arrival_times": "06:00:00", "departure_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.17] },
            "properties": {
              "kind": "stop", "route_id": "MR", "stop_id": "MR2",
              "trip_ids": ["MRL_MonFri_0"],
              "arrival_times": "06:20:00", "departure_times": "06:20:00"
            }
          }
        ]
      }
      """;

    const string saturdayMapGeoJson = """
      {
        "type": "FeatureCollection",
        "features": [
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.68, 3.13] },
            "properties": {
              "kind": "stop", "route_id": "MR", "stop_id": "MR1",
              "trip_ids": ["MRL_Sat_0"],
              "arrival_times": "06:00:00", "departure_times": "06:00:00"
            }
          },
          {
            "type": "Feature",
            "geometry": { "type": "Point", "coordinates": [101.70, 3.17] },
            "properties": {
              "kind": "stop", "route_id": "MR", "stop_id": "MR2",
              "trip_ids": ["MRL_Sat_0"],
              "arrival_times": "06:20:00", "departure_times": "06:20:00"
            }
          }
        ]
      }
      """;

    IReadOnlyList<StaticFrequency> frequencies =
    [
      new("MRL_MonFri_0", "8:00:00", "9:00:00", 600),
      new("MRL_MonFri_0", "9:00:00", "10:00:00", 600),
      new("MRL_MonFri_0", "23:40:00", "24:00:00", 600),
      new("MRL_Sat_0", "6:00:00", "23:30:00", 720)
    ];

    var fridayNetwork = ScheduledRailPositionEstimator.CompileForServiceDay(fridayMapGeoJson, frequencies);
    var saturdayNetwork = ScheduledRailPositionEstimator.CompileForServiceDay(saturdayMapGeoJson, frequencies);

    // At 09:00:00 on Friday: departures 08:40, 08:50, 09:00 are active (no duplicate 09:00!)
    var boundaryVehicles = ScheduledRailPositionEstimator.EstimateVehicles(
      fridayNetwork, null, RapidRail, new DateTimeOffset(2026, 6, 5, 9, 0, 0, MalaysiaTime.Offset));
    Assert.Equal(3, boundaryVehicles.Count);
    Assert.Equal(["MR-0-0840", "MR-0-0850", "MR-0-0900"], boundaryVehicles.Select(v => v.VehicleId ?? "").OrderBy(id => id).ToArray());
    Assert.All(boundaryVehicles, v => Assert.Equal("Rail", v.TransportType));

    // On Saturday at 00:05:00 MYT: Saturday service has not started, but Friday's 23:50 train is still running!
    var postMidnightVehicles = ScheduledRailPositionEstimator.EstimateVehicles(
      saturdayNetwork, fridayNetwork, RapidRail, new DateTimeOffset(2026, 6, 6, 0, 5, 0, MalaysiaTime.Offset));
    var nightTrain = Assert.Single(postMidnightVehicles);
    Assert.Equal("MR-0-2350", nightTrain.VehicleId);
    // 15 min into a 20 min straight-line segment = 75% progress
    Assert.InRange(nightTrain.Lat, 3.159f, 3.161f);
    Assert.InRange(nightTrain.Lng, 101.694f, 101.696f);
  }
}
