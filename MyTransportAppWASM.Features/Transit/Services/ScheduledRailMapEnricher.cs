using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MyTransportAppWASM.Features.Transit.Services;

internal static class ScheduledRailMapEnricher
{
  public static string Enrich(
    string mapGeoJson,
    IReadOnlyList<StaticFrequency> frequencies,
    string? routeIdFilter = null)
  {
    ArgumentNullException.ThrowIfNull(frequencies);

    var trimmedRouteFilter = routeIdFilter?.Trim();
    var hasRouteFilter = !string.IsNullOrEmpty(trimmedRouteFilter);

    if (string.IsNullOrWhiteSpace(mapGeoJson) || (!hasRouteFilter && frequencies.Count == 0))
    {
      return mapGeoJson;
    }

    var freqByTrip = frequencies.Count > 0
      ? ScheduledRailPositionEstimator.IndexFrequenciesByTrip(frequencies)
      : new Dictionary<string, List<CompiledFrequencyWindow>>(StringComparer.Ordinal);
    if (!hasRouteFilter && freqByTrip.Count == 0)
    {
      return mapGeoJson;
    }

    using var document = JsonDocument.Parse(mapGeoJson);
    if (document.RootElement.ValueKind != JsonValueKind.Object ||
        !document.RootElement.TryGetProperty("features", out var features) ||
        features.ValueKind != JsonValueKind.Array)
    {
      return mapGeoJson;
    }

    var routeTripIds = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
    var pendingStops = new List<StopScheduleFeature>();
    HashSet<int>? includedFeatures = hasRouteFilter ? [] : null;

    ScheduledRailPositionEstimator.ScanMapFeatures(
      features,
      trimmedRouteFilter,
      collectSchedules: freqByTrip.Count > 0,
      routeTripIds,
      pendingStops,
      includedFeatures);

    if (!hasRouteFilter && pendingStops.Count == 0)
    {
      return mapGeoJson;
    }

    Dictionary<string, string[]>? sortedRouteTrips = null;
    var tripFirstArrival = new Dictionary<string, int>(StringComparer.Ordinal);
    var parsedStops = new List<(int FeatureIndex, List<(string TripId, int ArrSecs)> Entries)>(pendingStops.Count);

    foreach (var stop in pendingStops)
    {
      if (!ScheduledRailPositionEstimator.TryResolveStopTripArrivals(
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

      var entries = new List<(string TripId, int ArrSecs)>(alignedTripIds.Count);
      for (var i = 0; i < alignedTripIds.Count; i++)
      {
        var tripId = alignedTripIds[i];
        if (!ScheduledRailPositionEstimator.TryParseGtfsTimeSecs(arrivals[i], out var arrSecs))
        {
          continue;
        }

        entries.Add((tripId, arrSecs));

        if (!tripFirstArrival.TryGetValue(tripId, out var firstArrival) || arrSecs < firstArrival)
        {
          tripFirstArrival[tripId] = arrSecs;
        }
      }

      if (entries.Count > 0)
      {
        parsedStops.Add((stop.FeatureIndex, entries));
      }
    }

    var stopWindows = ComputeStopArrivalWindows(parsedStops, freqByTrip, tripFirstArrival);

    if (!hasRouteFilter && stopWindows.Count == 0)
    {
      return mapGeoJson;
    }

    return RewriteGeoJson(document.RootElement, features, includedFeatures, stopWindows, mapGeoJson.Length);
  }

  private static Dictionary<int, List<CompiledFrequencyWindow>> ComputeStopArrivalWindows(
    List<(int FeatureIndex, List<(string TripId, int ArrSecs)> Entries)> parsedStops,
    Dictionary<string, List<CompiledFrequencyWindow>> freqByTrip,
    Dictionary<string, int> tripFirstArrival)
  {
    var stopWindows = new Dictionary<int, List<CompiledFrequencyWindow>>();
    foreach (var (index, entries) in parsedStops)
    {
      var hasFrequencyMatch = false;
      foreach (var entry in entries)
      {
        if (freqByTrip.ContainsKey(entry.TripId))
        {
          hasFrequencyMatch = true;
          break;
        }
      }

      if (!hasFrequencyMatch)
      {
        continue;
      }

      var arrivalWindows = new List<CompiledFrequencyWindow>();

      foreach (var (tripId, arrSecs) in entries)
      {
        if (freqByTrip.TryGetValue(tripId, out var windows) &&
            tripFirstArrival.TryGetValue(tripId, out var originSecs))
        {
          var arrOffset = arrSecs - originSecs;
          foreach (var freq in windows)
          {
            var arrStart = freq.StartSecs + arrOffset;
            var arrEnd = ScheduledRailPositionEstimator.ComputeLastWindowDeparture(freq) + arrOffset;
            if (arrStart >= 0 && arrEnd >= arrStart)
            {
              arrivalWindows.Add(new CompiledFrequencyWindow(arrStart, arrEnd, freq.HeadwaySecs));
            }
          }
        }
        else
        {
          arrivalWindows.Add(new CompiledFrequencyWindow(arrSecs, arrSecs, 1));
        }
      }

      if (arrivalWindows.Count > 0)
      {
        SortAndDeduplicateWindows(arrivalWindows);
        stopWindows[index] = arrivalWindows;
      }
    }

    return stopWindows;
  }

  private static string RewriteGeoJson(
    JsonElement root,
    JsonElement features,
    HashSet<int>? includedFeatures,
    Dictionary<int, List<CompiledFrequencyWindow>> stopWindows,
    int initialCapacity)
  {
    var buffer = new ArrayBufferWriter<byte>(initialCapacity);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      foreach (var rootProp in root.EnumerateObject())
      {
        if (!string.Equals(rootProp.Name, "features", StringComparison.Ordinal))
        {
          rootProp.WriteTo(writer);
          continue;
        }

        writer.WritePropertyName("features");
        writer.WriteStartArray();
        var currentIndex = 0;
        foreach (var feature in features.EnumerateArray())
        {
          if (includedFeatures != null && !includedFeatures.Contains(currentIndex))
          {
            currentIndex++;
            continue;
          }

          if (!stopWindows.TryGetValue(currentIndex, out var windows))
          {
            feature.WriteTo(writer);
          }
          else
          {
            writer.WriteStartObject();
            foreach (var featureProp in feature.EnumerateObject())
            {
              if (!string.Equals(featureProp.Name, "properties", StringComparison.Ordinal) ||
                  featureProp.Value.ValueKind != JsonValueKind.Object)
              {
                featureProp.WriteTo(writer);
                continue;
              }

              writer.WritePropertyName("properties");
              writer.WriteStartObject();
              foreach (var prop in featureProp.Value.EnumerateObject())
              {
                if (prop.Name == "frequency_arrivals")
                {
                  continue;
                }
                prop.WriteTo(writer);
              }
              WriteFrequencyWindowsProperty(writer, "frequency_arrivals", windows);
              writer.WriteEndObject();
            }
            writer.WriteEndObject();
          }
          currentIndex++;
        }
        writer.WriteEndArray();
      }
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }

  private static void SortAndDeduplicateWindows(List<CompiledFrequencyWindow> windows)
  {
    if (windows.Count <= 1) return;

    windows.Sort(ScheduledRailPositionEstimator.CompareFrequencyWindows);

    var write = 1;
    for (var read = 1; read < windows.Count; read++)
    {
      if (windows[read] != windows[write - 1])
      {
        windows[write++] = windows[read];
      }
    }
    if (write < windows.Count)
    {
      windows.RemoveRange(write, windows.Count - write);
    }
  }

  private static void WriteFrequencyWindowsProperty(
    Utf8JsonWriter writer,
    string propertyName,
    List<CompiledFrequencyWindow> windows)
  {
    if (windows.Count == 0) return;

    writer.WritePropertyName(propertyName);
    writer.WriteStartArray();
    foreach (var window in windows)
    {
      writer.WriteStartObject();
      writer.WriteNumber("start", window.StartSecs);
      writer.WriteNumber("end", window.EndSecs);
      writer.WriteNumber("headway", window.HeadwaySecs);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
  }
}
