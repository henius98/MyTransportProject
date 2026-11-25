using System.Globalization;
using System.Text.Json;

namespace MyTransportAppWASM.Features.Transit.Services;

internal sealed class ScheduledRailDayNetwork(
  IReadOnlyList<CompiledScheduledTrip> trips,
  IReadOnlyDictionary<string, string> routeShortNames)
{
  public IReadOnlyList<CompiledScheduledTrip> Trips { get; } = trips;
  public IReadOnlyDictionary<string, string> RouteShortNames { get; } = routeShortNames;
}

internal sealed record CompiledPolyline(
  (double Lat, double Lng)[] Points,
  double[] CumulativeMeters);

internal sealed record CompiledFrequencyWindow(
  int StartSecs,
  int EndSecs,
  int HeadwaySecs);

internal sealed record CompiledStopPoint(
  int ArrivalOffsetSecs,
  int DepartureOffsetSecs,
  double Lat,
  double Lng,
  bool HasCoords,
  double ShapeDistanceMeters,
  bool HasShapeDistance);

internal sealed record CompiledScheduledTrip(
  string TripId,
  string RouteId,
  string RouteShortName,
  string? TransportType,
  int DirectionId,
  int DurationSecs,
  IReadOnlyList<CompiledFrequencyWindow> Frequencies,
  CompiledStopPoint[] Stops,
  CompiledPolyline? Track);

internal static class ScheduledRailPositionEstimator
{
  private const double DegToRad = Math.PI / 180.0;
  private const double RadToDeg = 180.0 / Math.PI;
  private const double EarthRadiusMeters = 6371000.0;

  private sealed record TripMeta(string RouteId, string? ShapeId, int DirectionId);
  private sealed record RawTripStop(double Lat, double Lng, int ArrivalSecs, int DepartureSecs);

  public static ScheduledRailDayNetwork CompileForServiceDay(
    string mapGeoJson,
    IReadOnlyList<StaticFrequency> frequencies)
  {
    ArgumentNullException.ThrowIfNull(frequencies);

    if (string.IsNullOrWhiteSpace(mapGeoJson) || frequencies.Count == 0)
    {
      return new ScheduledRailDayNetwork([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    var freqByTrip = IndexFrequenciesByTrip(frequencies);
    if (freqByTrip.Count == 0)
    {
      return new ScheduledRailDayNetwork([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    var shapes = new Dictionary<string, CompiledPolyline>(StringComparer.Ordinal);
    var routeShortNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var tripMeta = new Dictionary<string, TripMeta>(StringComparer.Ordinal);
    var routeTripIds = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
    var tripStops = new Dictionary<string, List<RawTripStop>>(StringComparer.Ordinal);

    ParseMapGeoJson(mapGeoJson, shapes, routeShortNames, tripMeta, routeTripIds, tripStops);

    var compiledTrips = new List<CompiledScheduledTrip>(tripStops.Count);
    foreach (var (tripId, rawStops) in tripStops)
    {
      if (rawStops.Count < 2 || !freqByTrip.TryGetValue(tripId, out var windows) || windows.Count == 0)
      {
        continue;
      }

      if (!tripMeta.TryGetValue(tripId, out var meta))
      {
        continue;
      }

      rawStops.Sort(static (a, b) =>
      {
        var cmp = a.ArrivalSecs.CompareTo(b.ArrivalSecs);
        return cmp != 0 ? cmp : a.DepartureSecs.CompareTo(b.DepartureSecs);
      });

      CompiledPolyline? track = null;
      if (!string.IsNullOrWhiteSpace(meta.ShapeId))
      {
        shapes.TryGetValue(meta.ShapeId, out track);
      }

      var originSecs = rawStops[0].DepartureSecs;
      var compiledStops = CompileStops(rawStops, originSecs, track);
      var durationSecs = Math.Max(0, compiledStops[^1].DepartureOffsetSecs);
      var routeShortName = routeShortNames.TryGetValue(meta.RouteId, out var shortName)
        ? shortName
        : meta.RouteId;

      compiledTrips.Add(new CompiledScheduledTrip(
        tripId,
        meta.RouteId,
        routeShortName,
        ResolveTransportType(meta.RouteId, routeShortName),
        meta.DirectionId,
        durationSecs,
        windows,
        compiledStops,
        track));
    }

    return new ScheduledRailDayNetwork(compiledTrips, routeShortNames);
  }

  public static List<BusLocation> EstimateVehicles(
    ScheduledRailDayNetwork? todayNetwork,
    TransportProvider provider,
    DateTimeOffset utcNow)
  {
    ArgumentNullException.ThrowIfNull(provider);

    if (todayNetwork is not { Trips.Count: > 0 })
    {
      return [];
    }

    var localNow = MalaysiaTime.Convert(utcNow);
    var nowUnixSecs = (ulong)Math.Max(0, utcNow.ToUnixTimeSeconds());
    var nowSecsToday = (int)localNow.TimeOfDay.TotalSeconds;

    var vehicles = new List<BusLocation>();
    CollectForServiceDay(todayNetwork, provider, nowSecsToday, nowUnixSecs, vehicles);
    return vehicles;
  }

  private static void CollectForServiceDay(
    ScheduledRailDayNetwork network,
    TransportProvider provider,
    int nowSecs,
    ulong nowUnixSecs,
    List<BusLocation> vehicles)
  {
    foreach (var trip in network.Trips)
    {
      var firstArrivalOffset = trip.Stops[0].ArrivalOffsetSecs;
      var lastOffset = Math.Max(trip.DurationSecs, trip.Stops[^1].DepartureOffsetSecs);
      var minActiveDep = nowSecs - lastOffset;
      var maxActiveDep = nowSecs - firstArrivalOffset;

      HashSet<int>? seenDepartures = trip.Frequencies.Count > 1 ? [] : null;

      foreach (var freq in trip.Frequencies)
      {
        if (freq.HeadwaySecs <= 0 || freq.EndSecs <= freq.StartSecs)
        {
          continue;
        }

        // Match GTFS / worker half-open window [start_secs, end_secs).
        var lastWindowDep = freq.StartSecs + ((freq.EndSecs - freq.StartSecs - 1) / freq.HeadwaySecs) * freq.HeadwaySecs;
        var rangeStart = Math.Max(freq.StartSecs, minActiveDep);
        var rangeEnd = Math.Min(lastWindowDep, maxActiveDep);
        if (rangeStart > rangeEnd)
        {
          continue;
        }

        var delta = rangeStart - freq.StartSecs;
        var steps = (delta + freq.HeadwaySecs - 1) / freq.HeadwaySecs;
        var firstDep = freq.StartSecs + steps * freq.HeadwaySecs;

        for (var dep = firstDep; dep <= rangeEnd; dep += freq.HeadwaySecs)
        {
          if (seenDepartures != null && !seenDepartures.Add(dep))
          {
            continue;
          }

          var elapsed = nowSecs - dep;
          if (!TryInterpolatePosition(trip, elapsed, out var lat, out var lng, out var bearing, out var speedMps))
          {
            continue;
          }

          vehicles.Add(new BusLocation
          {
            ProviderName = provider.Name,
            TransportType = trip.TransportType ?? provider.TransportType,
            RouteId = trip.RouteId,
            RouteShortName = trip.RouteShortName,
            VehicleId = FormatVehicleId(trip.RouteShortName, trip.DirectionId, dep),
            Lat = (float)lat,
            Lng = (float)lng,
            Bearing = (float)bearing,
            Speed = (float)speedMps,
            Timestamp = nowUnixSecs,
            IsEstimated = true
          });
        }
      }
    }
  }

  private static Dictionary<string, List<CompiledFrequencyWindow>> IndexFrequenciesByTrip(
    IReadOnlyList<StaticFrequency> frequencies)
  {
    var byTrip = new Dictionary<string, List<CompiledFrequencyWindow>>(StringComparer.Ordinal);
    foreach (var freq in frequencies)
    {
      if (string.IsNullOrWhiteSpace(freq.TripId) || freq.HeadwaySecs <= 0)
      {
        continue;
      }

      if (!TryParseGtfsTimeSecs(freq.StartTime, out var startSecs) ||
          !TryParseGtfsTimeSecs(freq.EndTime, out var endSecs) ||
          endSecs <= startSecs)
      {
        continue;
      }

      var tripId = freq.TripId.Trim();
      if (!byTrip.TryGetValue(tripId, out var list))
      {
        list = [];
        byTrip[tripId] = list;
      }
      list.Add(new CompiledFrequencyWindow(startSecs, endSecs, freq.HeadwaySecs));
    }

    foreach (var list in byTrip.Values)
    {
      list.Sort(static (a, b) =>
      {
        var cmp = a.StartSecs.CompareTo(b.StartSecs);
        if (cmp != 0) return cmp;
        cmp = a.EndSecs.CompareTo(b.EndSecs);
        return cmp != 0 ? cmp : a.HeadwaySecs.CompareTo(b.HeadwaySecs);
      });
    }

    return byTrip;
  }

  private static void ParseMapGeoJson(
    string mapGeoJson,
    Dictionary<string, CompiledPolyline> shapes,
    Dictionary<string, string> routeShortNames,
    Dictionary<string, TripMeta> tripMeta,
    Dictionary<string, SortedSet<string>> routeTripIds,
    Dictionary<string, List<RawTripStop>> tripStops)
  {
    using var document = JsonDocument.Parse(mapGeoJson);
    if (!document.RootElement.TryGetProperty("features", out var features) ||
        features.ValueKind != JsonValueKind.Array)
    {
      return;
    }

    var pendingStops = new List<(string RouteId, double Lat, double Lng, List<string>? TripIds, string? Arrivals, string? Departures)>();

    foreach (var feature in features.EnumerateArray())
    {
      if (!feature.TryGetProperty("properties", out var props) ||
          !feature.TryGetProperty("geometry", out var geometry))
      {
        continue;
      }

      var kind = ReadString(props, "kind");
      if (string.Equals(kind, "route", StringComparison.OrdinalIgnoreCase))
      {
        var routeId = ReadString(props, "route_id");
        var shortName = ReadString(props, "route_short_name");
        if (!string.IsNullOrWhiteSpace(routeId) && !string.IsNullOrWhiteSpace(shortName))
        {
          routeShortNames.TryAdd(routeId, shortName);
        }

        var shapeId = ReadString(props, "shape_id");
        if (!string.IsNullOrWhiteSpace(shapeId) && !shapes.ContainsKey(shapeId) &&
            string.Equals(ReadString(geometry, "type"), "LineString", StringComparison.OrdinalIgnoreCase) &&
            geometry.TryGetProperty("coordinates", out var coords) &&
            coords.ValueKind == JsonValueKind.Array)
        {
          var points = new List<(double Lat, double Lng)>(coords.GetArrayLength());
          foreach (var pt in coords.EnumerateArray())
          {
            if (TryReadGeoJsonCoordinate(pt, out var lat, out var lng))
            {
              points.Add((lat, lng));
            }
          }

          if (points.Count >= 2)
          {
            shapes[shapeId] = BuildPolyline(points);
          }
        }

        if (!string.IsNullOrWhiteSpace(routeId))
        {
          var featureTripIds = ReadStringArray(props, "trip_ids");
          if (featureTripIds != null)
          {
            if (!routeTripIds.TryGetValue(routeId, out var set))
            {
              set = new SortedSet<string>(StringComparer.Ordinal);
              routeTripIds[routeId] = set;
            }

            foreach (var tripId in featureTripIds)
            {
              set.Add(tripId);
              tripMeta.TryAdd(tripId, new TripMeta(routeId, shapeId, InferDirectionId(tripId, shapeId)));
            }
          }
        }
      }
      else if (string.Equals(kind, "stop", StringComparison.OrdinalIgnoreCase))
      {
        var routeId = ReadString(props, "route_id");
        if (string.IsNullOrWhiteSpace(routeId) ||
            !string.Equals(ReadString(geometry, "type"), "Point", StringComparison.OrdinalIgnoreCase) ||
            !geometry.TryGetProperty("coordinates", out var coord) ||
            !TryReadGeoJsonCoordinate(coord, out var lat, out var lng))
        {
          continue;
        }

        pendingStops.Add((
          routeId,
          lat,
          lng,
          ReadStringArray(props, "trip_ids"),
          ReadString(props, "arrival_times"),
          ReadString(props, "departure_times")));
      }
    }

    var sortedRouteTrips = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var (routeId, set) in routeTripIds)
    {
      sortedRouteTrips[routeId] = set.ToArray();
    }

    foreach (var stop in pendingStops)
    {
      if (string.IsNullOrWhiteSpace(stop.Arrivals) || string.IsNullOrWhiteSpace(stop.Departures))
      {
        continue;
      }

      var arrivals = stop.Arrivals.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      var departures = stop.Departures.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      if (arrivals.Length == 0 || arrivals.Length != departures.Length)
      {
        continue;
      }

      // Prefer explicit per-stop trip_ids (supports asymmetric/express trips); fall back to sorted route trip_ids when symmetric.
      IReadOnlyList<string>? alignedTripIds = null;
      if (stop.TripIds != null && stop.TripIds.Count == arrivals.Length)
      {
        alignedTripIds = stop.TripIds;
      }
      else if (stop.TripIds == null &&
               sortedRouteTrips.TryGetValue(stop.RouteId, out var fallbackTripIds) &&
               fallbackTripIds.Length == arrivals.Length)
      {
        alignedTripIds = fallbackTripIds;
      }

      if (alignedTripIds == null)
      {
        continue;
      }

      for (var i = 0; i < alignedTripIds.Count; i++)
      {
        var tripId = alignedTripIds[i];
        if (!TryParseGtfsTimeSecs(arrivals[i], out var arrSecs) ||
            !TryParseGtfsTimeSecs(departures[i], out var depSecs) ||
            depSecs < arrSecs)
        {
          continue;
        }

        // Register trip metadata even for unshaped routes when stop trip_ids are present.
        tripMeta.TryAdd(tripId, new TripMeta(stop.RouteId, null, InferDirectionId(tripId, null)));

        if (!tripStops.TryGetValue(tripId, out var list))
        {
          list = [];
          tripStops[tripId] = list;
        }
        list.Add(new RawTripStop(stop.Lat, stop.Lng, arrSecs, depSecs));
      }
    }
  }

  private static bool TryInterpolatePosition(
    CompiledScheduledTrip trip,
    int elapsedSecs,
    out double lat,
    out double lng,
    out double bearing,
    out double speedMps)
  {
    var stops = trip.Stops;
    var first = stops[0];
    var last = stops[^1];

    if (elapsedSecs <= first.DepartureOffsetSecs)
    {
      return TryResolveDwell(trip, 0, out lat, out lng, out bearing, out speedMps);
    }

    if (elapsedSecs >= last.ArrivalOffsetSecs)
    {
      return TryResolveDwell(trip, stops.Length - 1, out lat, out lng, out bearing, out speedMps);
    }

    for (var i = 0; i < stops.Length - 1; i++)
    {
      var current = stops[i];
      var next = stops[i + 1];

      if (elapsedSecs <= current.DepartureOffsetSecs)
      {
        return TryResolveDwell(trip, i, out lat, out lng, out bearing, out speedMps);
      }

      if (elapsedSecs < next.ArrivalOffsetSecs)
      {
        var travelSecs = next.ArrivalOffsetSecs - current.DepartureOffsetSecs;
        var fraction = travelSecs > 0
          ? Math.Clamp((elapsedSecs - current.DepartureOffsetSecs) / (double)travelSecs, 0.0, 1.0)
          : 0.0;

        if (trip.Track != null &&
            current.HasShapeDistance &&
            next.HasShapeDistance &&
            next.ShapeDistanceMeters > current.ShapeDistanceMeters)
        {
          var segMeters = next.ShapeDistanceMeters - current.ShapeDistanceMeters;
          var targetDist = current.ShapeDistanceMeters + fraction * segMeters;
          SampleTrackAtDistance(trip.Track, targetDist, out lat, out lng, out bearing);
          speedMps = travelSecs > 0 ? segMeters / travelSecs : 0.0;
          return true;
        }

        if (current.HasCoords && next.HasCoords)
        {
          lat = current.Lat + (next.Lat - current.Lat) * fraction;
          lng = current.Lng + (next.Lng - current.Lng) * fraction;
          bearing = ComputeBearingDegrees(current.Lat, current.Lng, next.Lat, next.Lng);
          speedMps = travelSecs > 0
            ? GeoMath.DistanceMeters(current.Lat, current.Lng, next.Lat, next.Lng) / travelSecs
            : 0.0;
          return true;
        }

        lat = lng = bearing = speedMps = 0.0;
        return false;
      }
    }

    return TryResolveDwell(trip, stops.Length - 1, out lat, out lng, out bearing, out speedMps);
  }

  private static bool TryResolveDwell(
    CompiledScheduledTrip trip,
    int stopIndex,
    out double lat,
    out double lng,
    out double bearing,
    out double speedMps)
  {
    var stop = trip.Stops[stopIndex];
    speedMps = 0.0;

    if (trip.Track != null && stop.HasShapeDistance)
    {
      SampleTrackAtDistance(trip.Track, stop.ShapeDistanceMeters, out lat, out lng, out bearing);
      return true;
    }

    if (stop.HasCoords)
    {
      lat = stop.Lat;
      lng = stop.Lng;
      bearing = ResolveStopFallbackBearing(trip.Stops, stopIndex);
      return true;
    }

    lat = lng = bearing = 0.0;
    return false;
  }

  private static double ResolveStopFallbackBearing(CompiledStopPoint[] stops, int stopIndex)
  {
    var current = stops[stopIndex];
    if (stopIndex + 1 < stops.Length && stops[stopIndex + 1].HasCoords)
    {
      var next = stops[stopIndex + 1];
      return ComputeBearingDegrees(current.Lat, current.Lng, next.Lat, next.Lng);
    }

    if (stopIndex > 0 && stops[stopIndex - 1].HasCoords)
    {
      var prev = stops[stopIndex - 1];
      return ComputeBearingDegrees(prev.Lat, prev.Lng, current.Lat, current.Lng);
    }

    return 0.0;
  }

  private static void SampleTrackAtDistance(
    CompiledPolyline track,
    double targetMeters,
    out double lat,
    out double lng,
    out double bearing)
  {
    var cum = track.CumulativeMeters;
    var pts = track.Points;
    var lastIdx = pts.Length - 1;

    if (targetMeters <= 0.0)
    {
      lat = pts[0].Lat;
      lng = pts[0].Lng;
      bearing = ComputeBearingDegrees(pts[0].Lat, pts[0].Lng, pts[1].Lat, pts[1].Lng);
      return;
    }

    if (targetMeters >= cum[lastIdx])
    {
      lat = pts[lastIdx].Lat;
      lng = pts[lastIdx].Lng;
      bearing = ComputeBearingDegrees(pts[lastIdx - 1].Lat, pts[lastIdx - 1].Lng, pts[lastIdx].Lat, pts[lastIdx].Lng);
      return;
    }

    var idx = Array.BinarySearch(cum, targetMeters);
    if (idx >= 0)
    {
      lat = pts[idx].Lat;
      lng = pts[idx].Lng;
      var segStart = idx < lastIdx ? idx : lastIdx - 1;
      bearing = ComputeBearingDegrees(pts[segStart].Lat, pts[segStart].Lng, pts[segStart + 1].Lat, pts[segStart + 1].Lng);
      return;
    }

    var nextIdx = ~idx;
    var prevIdx = Math.Max(0, nextIdx - 1);
    var segLen = cum[nextIdx] - cum[prevIdx];
    var frac = segLen > 0.0 ? Math.Clamp((targetMeters - cum[prevIdx]) / segLen, 0.0, 1.0) : 0.0;

    lat = pts[prevIdx].Lat + (pts[nextIdx].Lat - pts[prevIdx].Lat) * frac;
    lng = pts[prevIdx].Lng + (pts[nextIdx].Lng - pts[prevIdx].Lng) * frac;
    bearing = ComputeBearingDegrees(pts[prevIdx].Lat, pts[prevIdx].Lng, pts[nextIdx].Lat, pts[nextIdx].Lng);
  }

  private static CompiledStopPoint[] CompileStops(
    IReadOnlyList<RawTripStop> rawStops,
    int originSecs,
    CompiledPolyline? track)
  {
    var compiled = new CompiledStopPoint[rawStops.Count];
    var minSegmentIndex = 0;
    var prevDist = 0.0;

    for (var i = 0; i < rawStops.Count; i++)
    {
      var st = rawStops[i];
      var hasShapeDist = false;
      var shapeDist = 0.0;

      if (track != null)
      {
        var (projectedDist, segIndex) = ProjectOntoTrack(track, st.Lat, st.Lng, minSegmentIndex);
        shapeDist = Math.Max(prevDist, projectedDist);
        prevDist = shapeDist;
        minSegmentIndex = segIndex;
        hasShapeDist = true;
      }

      compiled[i] = new CompiledStopPoint(
        st.ArrivalSecs - originSecs,
        st.DepartureSecs - originSecs,
        st.Lat,
        st.Lng,
        HasCoords: true,
        shapeDist,
        hasShapeDist);
    }

    return compiled;
  }

  private static (double DistanceMeters, int SegmentIndex) ProjectOntoTrack(
    CompiledPolyline track,
    double lat,
    double lng,
    int startSegmentIndex)
  {
    var pts = track.Points;
    var cum = track.CumulativeMeters;
    var cosLat = Math.Cos(lat * DegToRad);
    var bestDistSq = double.PositiveInfinity;
    var bestDistanceAlong = cum[Math.Clamp(startSegmentIndex, 0, pts.Length - 1)];
    var bestSegmentIndex = startSegmentIndex;

    for (var i = startSegmentIndex; i < pts.Length - 1; i++)
    {
      var a = pts[i];
      var b = pts[i + 1];

      var ax = (a.Lng - lng) * cosLat;
      var ay = a.Lat - lat;
      var bx = (b.Lng - lng) * cosLat;
      var by = b.Lat - lat;

      var dx = bx - ax;
      var dy = by - ay;
      var lenSq = dx * dx + dy * dy;
      var t = lenSq > 1e-18 ? Math.Clamp(-(ax * dx + ay * dy) / lenSq, 0.0, 1.0) : 0.0;

      var px = ax + t * dx;
      var py = ay + t * dy;
      var distSq = px * px + py * py;

      if (distSq < bestDistSq)
      {
        bestDistSq = distSq;
        bestSegmentIndex = i;
        bestDistanceAlong = cum[i] + t * (cum[i + 1] - cum[i]);
      }
    }

    return (bestDistanceAlong, bestSegmentIndex);
  }

  private static CompiledPolyline BuildPolyline(List<(double Lat, double Lng)> points)
  {
    var arr = points.ToArray();
    var cum = new double[arr.Length];
    var total = 0.0;

    for (var i = 1; i < arr.Length; i++)
    {
      total += FastDistanceMeters(arr[i - 1].Lat, arr[i - 1].Lng, arr[i].Lat, arr[i].Lng);
      cum[i] = total;
    }

    return new CompiledPolyline(arr, cum);
  }

  internal static bool TryParseGtfsTimeSecs(string? raw, out int seconds)
  {
    seconds = 0;
    if (string.IsNullOrWhiteSpace(raw))
    {
      return false;
    }

    var trimmed = raw.AsSpan().Trim();
    var firstColon = trimmed.IndexOf(':');
    if (firstColon <= 0)
    {
      return false;
    }

    var rest = trimmed[(firstColon + 1)..];
    var secondColon = rest.IndexOf(':');
    if (secondColon <= 0)
    {
      return false;
    }

    if (!int.TryParse(trimmed[..firstColon], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
        !int.TryParse(rest[..secondColon], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
        !int.TryParse(rest[(secondColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var secs) ||
        minutes is < 0 or >= 60 ||
        secs is < 0 or >= 60)
    {
      return false;
    }

    seconds = hours * 3600 + minutes * 60 + secs;
    return true;
  }

  private static int InferDirectionId(string tripId, string? shapeId)
  {
    if (TryReadTrailingDigit(tripId, out var tripDir)) return tripDir;
    if (!string.IsNullOrEmpty(shapeId) && TryReadTrailingDigit(shapeId, out var shapeDir)) return shapeDir;
    return 0;
  }

  private static bool TryReadTrailingDigit(string value, out int digit)
  {
    digit = 0;
    var sep = value.LastIndexOf('_');
    if (sep >= 0 && sep == value.Length - 2 && value[^1] is '0' or '1')
    {
      digit = value[^1] - '0';
      return true;
    }
    return false;
  }

  private static bool TryReadGeoJsonCoordinate(JsonElement coord, out double lat, out double lng)
  {
    lat = 0;
    lng = 0;
    if (coord.ValueKind != JsonValueKind.Array || coord.GetArrayLength() < 2)
    {
      return false;
    }

    // GeoJSON coordinates are [longitude, latitude].
    if (!coord[0].TryGetDouble(out lng) || !coord[1].TryGetDouble(out lat))
    {
      return false;
    }

    return GeoMath.IsValidCoordinates(lat, lng);
  }

  private static string? ResolveTransportType(string routeId, string routeShortName)
  {
    var key = routeId.Trim().ToUpperInvariant();
    return key switch
    {
      "AG" or "KJ" or "PH" or "SA" => "LRT",
      "KGL" or "PYL" => "MRT",
      "BRT" => "Bus",
      _ => routeShortName.Trim().ToUpperInvariant() switch
      {
        "AGL" or "KJL" or "SPL" or "SAL" => "LRT",
        "KGL" or "PYL" => "MRT",
        "BRT" => "Bus",
        _ => null
      }
    };
  }

  private static string FormatVehicleId(string routeShortName, int directionId, int departureSecs)
  {
    var hours = departureSecs / 3600;
    var minutes = (departureSecs % 3600) / 60;
    var seconds = departureSecs % 60;
    return seconds == 0
      ? $"{routeShortName}-{directionId}-{hours:D2}{minutes:D2}"
      : $"{routeShortName}-{directionId}-{hours:D2}{minutes:D2}{seconds:D2}";
  }

  private static double FastDistanceMeters(double lat1, double lng1, double lat2, double lng2)
  {
    var x = (lng2 - lng1) * DegToRad * Math.Cos(((lat1 + lat2) * 0.5) * DegToRad);
    var y = (lat2 - lat1) * DegToRad;
    return Math.Sqrt(x * x + y * y) * EarthRadiusMeters;
  }

  private static double ComputeBearingDegrees(double lat1, double lng1, double lat2, double lng2)
  {
    if (Math.Abs(lat2 - lat1) < 1e-12 && Math.Abs(lng2 - lng1) < 1e-12)
    {
      return 0.0;
    }

    var phi1 = lat1 * DegToRad;
    var phi2 = lat2 * DegToRad;
    var dLon = (lng2 - lng1) * DegToRad;
    var y = Math.Sin(dLon) * Math.Cos(phi2);
    var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
    return (Math.Atan2(y, x) * RadToDeg + 360.0) % 360.0;
  }

  private static string? ReadString(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
      ? value.GetString()?.Trim()
      : null;

  private static List<string>? ReadStringArray(JsonElement element, string property)
  {
    if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
    {
      return null;
    }

    var list = new List<string>(value.GetArrayLength());
    foreach (var item in value.EnumerateArray())
    {
      if (item.ValueKind == JsonValueKind.String)
      {
        var text = item.GetString()?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
          list.Add(text);
        }
      }
    }
    return list;
  }
}
