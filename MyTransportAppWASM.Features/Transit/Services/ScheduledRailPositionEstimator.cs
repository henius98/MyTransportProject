using System.Globalization;
using System.Text.Json;

namespace MyTransportAppWASM.Features.Transit.Services;

internal sealed class ScheduledRailDayNetwork(IReadOnlyList<CompiledScheduledTrip> trips)
{
  public static readonly ScheduledRailDayNetwork Empty = new([]);
  public IReadOnlyList<CompiledScheduledTrip> Trips { get; } = trips;
}

internal sealed record CompiledPolyline(
  (double Lat, double Lng)[] Points,
  double[] CumulativeMeters);

internal sealed record CompiledFrequencyWindow(
  int StartSecs,
  int EndSecs,
  int HeadwaySecs);

internal readonly record struct StopScheduleFeature(
  int FeatureIndex,
  string RouteId,
  List<string>? TripIds,
  string ArrivalsRaw,
  JsonElement? Geometry);

internal sealed record CompiledStopPoint(
  int ArrivalOffsetSecs,
  double Lat,
  double Lng,
  double ShapeDistanceMeters);

internal sealed record CompiledScheduledTrip(
  string RouteId,
  string RouteShortName,
  string? TransportType,
  int DirectionId,
  IReadOnlyList<CompiledFrequencyWindow> Frequencies,
  CompiledStopPoint[] Stops,
  CompiledPolyline? Track)
{
  public int DurationSecs => Stops[^1].ArrivalOffsetSecs;
}

internal static class ScheduledRailPositionEstimator
{
  private sealed record TripMeta(string RouteId, string? ShapeId, int DirectionId);
  private sealed record RawTripStop(double Lat, double Lng, int ArrivalSecs);

  public static ScheduledRailDayNetwork CompileForServiceDay(
    string mapGeoJson,
    IReadOnlyList<StaticFrequency> frequencies)
  {
    ArgumentNullException.ThrowIfNull(frequencies);

    if (string.IsNullOrWhiteSpace(mapGeoJson) || frequencies.Count == 0)
    {
      return ScheduledRailDayNetwork.Empty;
    }

    var freqByTrip = IndexFrequenciesByTrip(frequencies);
    if (freqByTrip.Count == 0)
    {
      return ScheduledRailDayNetwork.Empty;
    }

    var shapes = new Dictionary<string, CompiledPolyline>(StringComparer.Ordinal);
    var routeShortNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var tripMeta = new Dictionary<string, TripMeta>(StringComparer.Ordinal);
    var routeTripIds = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
    var tripStops = new Dictionary<string, List<RawTripStop>>(StringComparer.Ordinal);

    ParseMapGeoJson(mapGeoJson, freqByTrip, shapes, routeShortNames, tripMeta, routeTripIds, tripStops);

    var compiledTrips = new List<CompiledScheduledTrip>(tripStops.Count);
    foreach (var (tripId, rawStops) in tripStops)
    {
      if (rawStops.Count < 2)
      {
        continue;
      }

      rawStops.Sort(static (a, b) => a.ArrivalSecs.CompareTo(b.ArrivalSecs));

      var meta = tripMeta[tripId];
      CompiledPolyline? track = null;
      if (!string.IsNullOrWhiteSpace(meta.ShapeId))
      {
        shapes.TryGetValue(meta.ShapeId, out track);
      }

      var compiledStops = CompileStops(rawStops, rawStops[0].ArrivalSecs, track);
      var routeShortName = routeShortNames.TryGetValue(meta.RouteId, out var shortName)
        ? shortName
        : meta.RouteId;

      compiledTrips.Add(new CompiledScheduledTrip(
        meta.RouteId,
        routeShortName,
        ResolveTransportType(meta.RouteId, routeShortName),
        meta.DirectionId,
        freqByTrip[tripId],
        compiledStops,
        track));
    }

    return new ScheduledRailDayNetwork(compiledTrips);
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

    var nowUnixSecs = (ulong)Math.Max(0, utcNow.ToUnixTimeSeconds());
    var nowSecs = (int)MalaysiaTime.Convert(utcNow).TimeOfDay.TotalSeconds;
    var vehicles = new List<BusLocation>();

    foreach (var trip in todayNetwork.Trips)
    {
      var minActiveDep = nowSecs - trip.DurationSecs;
      HashSet<int>? seenDepartures = trip.Frequencies.Count > 1 ? [] : null;

      foreach (var freq in trip.Frequencies)
      {
        // Match GTFS / worker half-open window [start_secs, end_secs).
        var lastWindowDep = ComputeLastWindowDeparture(freq);
        var rangeStart = Math.Max(freq.StartSecs, minActiveDep);
        var rangeEnd = Math.Min(lastWindowDep, nowSecs);
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

          var (lat, lng, bearing, speedMps) = InterpolatePosition(trip, nowSecs - dep);
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

    return vehicles;
  }

  internal static int CompareFrequencyWindows(CompiledFrequencyWindow a, CompiledFrequencyWindow b)
  {
    var cmp = a.StartSecs.CompareTo(b.StartSecs);
    if (cmp != 0) return cmp;
    cmp = a.EndSecs.CompareTo(b.EndSecs);
    return cmp != 0 ? cmp : a.HeadwaySecs.CompareTo(b.HeadwaySecs);
  }

  internal static Dictionary<string, List<CompiledFrequencyWindow>> IndexFrequenciesByTrip(
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
      list.Sort(CompareFrequencyWindows);
    }

    return byTrip;
  }

  internal static void ScanMapFeatures(
    JsonElement features,
    string? routeIdFilter,
    bool collectSchedules,
    Dictionary<string, SortedSet<string>> routeTripIds,
    List<StopScheduleFeature> pendingStops,
    HashSet<int>? includedFeatures = null,
    Action<string, JsonElement, JsonElement, List<string>?>? onRouteFeature = null)
  {
    var hasRouteFilter = !string.IsNullOrEmpty(routeIdFilter);
    var featureIndex = 0;

    foreach (var feature in features.EnumerateArray())
    {
      if (feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
      {
        var routeId = ReadString(props, "route_id");
        if (hasRouteFilter && !string.Equals(routeId, routeIdFilter, StringComparison.OrdinalIgnoreCase))
        {
          featureIndex++;
          continue;
        }

        includedFeatures?.Add(featureIndex);

        if (collectSchedules)
        {
          var hasGeometry = feature.TryGetProperty("geometry", out var geometry);
          var kind = ReadString(props, "kind");
          if (string.Equals(kind, "route", StringComparison.OrdinalIgnoreCase))
          {
            var featureTripIds = ReadStringArray(props, "trip_ids");
            if (!string.IsNullOrWhiteSpace(routeId) && featureTripIds != null)
            {
              if (!routeTripIds.TryGetValue(routeId, out var set))
              {
                set = new SortedSet<string>(StringComparer.Ordinal);
                routeTripIds[routeId] = set;
              }
              foreach (var tripId in featureTripIds)
              {
                set.Add(tripId);
              }
            }

            if (hasGeometry && onRouteFeature != null && !string.IsNullOrWhiteSpace(routeId))
            {
              onRouteFeature(routeId, props, geometry, featureTripIds);
            }
          }
          else if (string.Equals(kind, "stop", StringComparison.OrdinalIgnoreCase))
          {
            var arrivalsRaw = ReadString(props, "arrival_times");
            if (!string.IsNullOrWhiteSpace(routeId) && !string.IsNullOrWhiteSpace(arrivalsRaw))
            {
              pendingStops.Add(new StopScheduleFeature(
                featureIndex,
                routeId,
                ReadStringArray(props, "trip_ids"),
                arrivalsRaw,
                hasGeometry ? geometry : null));
            }
          }
        }
      }

      featureIndex++;
    }
  }

  private static void ParseMapGeoJson(
    string mapGeoJson,
    Dictionary<string, List<CompiledFrequencyWindow>> freqByTrip,
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

    var pendingStops = new List<StopScheduleFeature>();

    ScanMapFeatures(
      features,
      routeIdFilter: null,
      collectSchedules: true,
      routeTripIds,
      pendingStops,
      onRouteFeature: (routeId, props, geometry, featureTripIds) =>
      {
        var shortName = ReadString(props, "route_short_name");
        if (!string.IsNullOrWhiteSpace(shortName))
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

        if (featureTripIds != null)
        {
          foreach (var tripId in featureTripIds)
          {
            tripMeta.TryAdd(tripId, new TripMeta(routeId, shapeId, InferDirectionId(tripId, shapeId)));
          }
        }
      });

    Dictionary<string, string[]>? sortedRouteTrips = null;

    foreach (var stop in pendingStops)
    {
      if (stop.Geometry is not { } geometry ||
          !string.Equals(ReadString(geometry, "type"), "Point", StringComparison.OrdinalIgnoreCase) ||
          !geometry.TryGetProperty("coordinates", out var coord) ||
          !TryReadGeoJsonCoordinate(coord, out var lat, out var lng))
      {
        continue;
      }

      if (!TryResolveStopTripArrivals(
            stop.ArrivalsRaw,
            stop.TripIds,
            stop.RouteId,
            routeTripIds,
            ref sortedRouteTrips,
            out var alignedTripIds,
            out var arrivals))
      {
        continue;
      }

      for (var i = 0; i < alignedTripIds.Count; i++)
      {
        var tripId = alignedTripIds[i];
        if (!freqByTrip.ContainsKey(tripId) || !TryParseGtfsTimeSecs(arrivals[i], out var arrSecs))
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
        list.Add(new RawTripStop(lat, lng, arrSecs));
      }
    }
  }

  internal static int ComputeLastWindowDeparture(CompiledFrequencyWindow freq) =>
    freq.StartSecs + ((freq.EndSecs - freq.StartSecs - 1) / freq.HeadwaySecs) * freq.HeadwaySecs;

  internal static bool TryResolveStopTripArrivals(
    string arrivalsRaw,
    List<string>? stopTripIds,
    string routeId,
    Dictionary<string, SortedSet<string>> routeTripIds,
    ref Dictionary<string, string[]>? sortedRouteTrips,
    out IReadOnlyList<string> alignedTripIds,
    out string[] arrivals)
  {
    alignedTripIds = [];
    arrivals = arrivalsRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (arrivals.Length == 0)
    {
      return false;
    }

    // Prefer explicit per-stop trip_ids (supports asymmetric/express trips); fall back to sorted route trip_ids when symmetric.
    if (stopTripIds != null && stopTripIds.Count == arrivals.Length)
    {
      alignedTripIds = stopTripIds;
      return true;
    }

    if (stopTripIds == null)
    {
      sortedRouteTrips ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
      if (!sortedRouteTrips.TryGetValue(routeId, out var fallbackTripIds) &&
          routeTripIds.TryGetValue(routeId, out var set))
      {
        fallbackTripIds = set.ToArray();
        sortedRouteTrips[routeId] = fallbackTripIds;
      }

      if (fallbackTripIds != null && fallbackTripIds.Length == arrivals.Length)
      {
        alignedTripIds = fallbackTripIds;
        return true;
      }
    }

    return false;
  }

  private static (double Lat, double Lng, double Bearing, double SpeedMps) InterpolatePosition(
    CompiledScheduledTrip trip,
    int elapsedSecs)
  {
    var stops = trip.Stops;

    if (elapsedSecs <= stops[0].ArrivalOffsetSecs)
    {
      return ResolveAtStop(trip, 0);
    }

    if (elapsedSecs >= stops[^1].ArrivalOffsetSecs)
    {
      return ResolveAtStop(trip, stops.Length - 1);
    }

    for (var i = 0; i < stops.Length - 1; i++)
    {
      var current = stops[i];
      if (elapsedSecs <= current.ArrivalOffsetSecs)
      {
        return ResolveAtStop(trip, i);
      }

      var next = stops[i + 1];
      if (elapsedSecs >= next.ArrivalOffsetSecs)
      {
        continue;
      }

      var travelSecs = next.ArrivalOffsetSecs - current.ArrivalOffsetSecs;
      var fraction = (elapsedSecs - current.ArrivalOffsetSecs) / (double)travelSecs;

      if (trip.Track != null && next.ShapeDistanceMeters > current.ShapeDistanceMeters)
      {
        var segMeters = next.ShapeDistanceMeters - current.ShapeDistanceMeters;
        var (lat, lng, bearing) = SampleTrackAtDistance(trip.Track, current.ShapeDistanceMeters + fraction * segMeters);
        return (lat, lng, bearing, segMeters / travelSecs);
      }

      return (
        current.Lat + (next.Lat - current.Lat) * fraction,
        current.Lng + (next.Lng - current.Lng) * fraction,
        ComputeBearingDegrees(current.Lat, current.Lng, next.Lat, next.Lng),
        GeoMath.DistanceMeters(current.Lat, current.Lng, next.Lat, next.Lng) / travelSecs);
    }

    return ResolveAtStop(trip, stops.Length - 1);
  }

  private static (double Lat, double Lng, double Bearing, double SpeedMps) ResolveAtStop(
    CompiledScheduledTrip trip,
    int stopIndex)
  {
    var stop = trip.Stops[stopIndex];
    if (trip.Track != null)
    {
      var (lat, lng, bearing) = SampleTrackAtDistance(trip.Track, stop.ShapeDistanceMeters);
      return (lat, lng, bearing, 0.0);
    }

    return (stop.Lat, stop.Lng, ResolveStopFallbackBearing(trip.Stops, stopIndex), 0.0);
  }

  private static double ResolveStopFallbackBearing(CompiledStopPoint[] stops, int stopIndex) =>
    stopIndex + 1 < stops.Length
      ? ComputeBearingDegrees(stops[stopIndex].Lat, stops[stopIndex].Lng, stops[stopIndex + 1].Lat, stops[stopIndex + 1].Lng)
      : ComputeBearingDegrees(stops[stopIndex - 1].Lat, stops[stopIndex - 1].Lng, stops[stopIndex].Lat, stops[stopIndex].Lng);

  private static (double Lat, double Lng, double Bearing) SampleTrackAtDistance(
    CompiledPolyline track,
    double targetMeters)
  {
    var cum = track.CumulativeMeters;
    var pts = track.Points;
    var lastIdx = pts.Length - 1;

    if (targetMeters <= 0.0)
    {
      return (pts[0].Lat, pts[0].Lng, ComputeBearingDegrees(pts[0].Lat, pts[0].Lng, pts[1].Lat, pts[1].Lng));
    }

    if (targetMeters >= cum[lastIdx])
    {
      return (pts[lastIdx].Lat, pts[lastIdx].Lng, ComputeBearingDegrees(pts[lastIdx - 1].Lat, pts[lastIdx - 1].Lng, pts[lastIdx].Lat, pts[lastIdx].Lng));
    }

    var idx = Array.BinarySearch(cum, targetMeters);
    if (idx >= 0)
    {
      var segStart = idx < lastIdx ? idx : lastIdx - 1;
      return (pts[idx].Lat, pts[idx].Lng, ComputeBearingDegrees(pts[segStart].Lat, pts[segStart].Lng, pts[segStart + 1].Lat, pts[segStart + 1].Lng));
    }

    var nextIdx = ~idx;
    var prevIdx = Math.Max(0, nextIdx - 1);
    var segLen = cum[nextIdx] - cum[prevIdx];
    var frac = segLen > 0.0 ? Math.Clamp((targetMeters - cum[prevIdx]) / segLen, 0.0, 1.0) : 0.0;

    return (
      pts[prevIdx].Lat + (pts[nextIdx].Lat - pts[prevIdx].Lat) * frac,
      pts[prevIdx].Lng + (pts[nextIdx].Lng - pts[prevIdx].Lng) * frac,
      ComputeBearingDegrees(pts[prevIdx].Lat, pts[prevIdx].Lng, pts[nextIdx].Lat, pts[nextIdx].Lng));
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
      var shapeDist = 0.0;

      if (track != null)
      {
        var (projectedDist, segIndex) = ProjectOntoTrack(track, st.Lat, st.Lng, minSegmentIndex);
        shapeDist = Math.Max(prevDist, projectedDist);
        prevDist = shapeDist;
        minSegmentIndex = segIndex;
      }

      compiled[i] = new CompiledStopPoint(st.ArrivalSecs - originSecs, st.Lat, st.Lng, shapeDist);
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
    var cosLat = Math.Cos(double.DegreesToRadians(lat));
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
      total += GeoMath.DistanceMeters(arr[i - 1].Lat, arr[i - 1].Lng, arr[i].Lat, arr[i].Lng);
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

  private static double ComputeBearingDegrees(double lat1, double lng1, double lat2, double lng2)
  {
    if (Math.Abs(lat2 - lat1) < 1e-12 && Math.Abs(lng2 - lng1) < 1e-12)
    {
      return 0.0;
    }

    var phi1 = double.DegreesToRadians(lat1);
    var phi2 = double.DegreesToRadians(lat2);
    var dLon = double.DegreesToRadians(lng2 - lng1);
    var y = Math.Sin(dLon) * Math.Cos(phi2);
    var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
    return (double.RadiansToDegrees(Math.Atan2(y, x)) + 360.0) % 360.0;
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
