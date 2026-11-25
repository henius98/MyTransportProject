namespace MyTransportAppWASM.Features.Transit.Services;

internal static class LiveRouting
{
  public static List<EnrichedRoute> EnrichRoutes(List<GoogleRoute> googleRoutes, List<BusLocation> liveVehicles)
  {
    ArgumentNullException.ThrowIfNull(googleRoutes);
    ArgumentNullException.ThrowIfNull(liveVehicles);

    var vehiclesByRoute = IndexVehiclesByRoute(liveVehicles);
    var result = new List<EnrichedRoute>(googleRoutes.Count);

    foreach (var route in googleRoutes)
    {
      var enriched = new EnrichedRoute
      {
        OriginalRoute = route,
        LiveTotalDurationSeconds = route.DurationSeconds,
        HasLiveBus = false
      };

      for (int stepIndex = 0; stepIndex < route.TransitSteps.Count; stepIndex++)
      {
        var step = route.TransitSteps[stepIndex];
        var matchingBuses = FindMatchingVehicles(vehiclesByRoute, step);
        if (matchingBuses is null)
        {
          continue;
        }

        BusLocation? closestBus = null;
        double closestDistance = double.MaxValue;
        foreach (var bus in matchingBuses)
        {
          var dist = GeoMath.DistanceMeters(bus.Lat, bus.Lng, step.DepartureLat, step.DepartureLng);
          if (dist < closestDistance)
          {
            closestDistance = dist;
            closestBus = bus;
          }
        }

        if (closestBus is null)
        {
          continue;
        }

        // Rough ETA: assume 20 km/h average speed (approx 5.5 m/s) in city if straight line.
        int estimatedSeconds = (int)(closestDistance / 5.5);

        enriched.NearestBuses.Add(new LiveBusInfo
        {
          VehicleId = closestBus.VehicleId ?? "",
          RouteId = !string.IsNullOrWhiteSpace(closestBus.RouteShortName)
            ? closestBus.RouteShortName
            : closestBus.RouteId ?? "",
          TransportType = closestBus.TransportType ?? "",
          Lat = closestBus.Lat,
          Lng = closestBus.Lng,
          DistanceToStopMeters = closestDistance,
          EstimatedArrivalSeconds = estimatedSeconds
        });
        enriched.HasLiveBus = true;

        // Preserve the existing behavior of adding each matched step's wait estimate.
        // Note: scoped to the first transit step as subsequent connections are modeled by the base route.
        if (stepIndex == 0)
        {
          enriched.LiveTotalDurationSeconds += estimatedSeconds;
        }
      }

      result.Add(enriched);
    }

    // Rank the routes: those with live buses and lowest live duration first, then those without live buses
    result.Sort(static (a, b) =>
    {
      var liveBusComparison = b.HasLiveBus.CompareTo(a.HasLiveBus);
      if (liveBusComparison != 0)
      {
        return liveBusComparison;
      }

      return a.LiveTotalDurationSeconds.CompareTo(b.LiveTotalDurationSeconds);
    });

    return result;
  }

  /// <summary>
  /// Filters live vehicles to only those serving transit lines involved in the specified route.
  /// </summary>
  public static List<BusLocation> FilterVehiclesForRoute(
    GoogleRoute route,
    IReadOnlyList<BusLocation> liveVehicles)
  {
    ArgumentNullException.ThrowIfNull(route);
    ArgumentNullException.ThrowIfNull(liveVehicles);

    return FilterVehiclesByRouteIdentifiers(liveVehicles, CollectRouteIdentifiers(route));
  }

  /// <summary>
  /// Filters live vehicles to only those belonging to the selected static provider and route.
  /// </summary>
  public static List<BusLocation> FilterVehiclesForStaticRoute(
    string providerName,
    string routeId,
    IReadOnlyList<StaticRoute> staticRoutes,
    IReadOnlyList<BusLocation> liveVehicles)
  {
    ArgumentNullException.ThrowIfNull(staticRoutes);
    ArgumentNullException.ThrowIfNull(liveVehicles);

    if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(routeId))
    {
      return [];
    }

    var trimmedRouteId = routeId.Trim();
    var allowedRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    AddRouteAliases(allowedRouteIds, trimmedRouteId);

    foreach (var route in staticRoutes)
    {
      if (string.Equals(route.RouteId?.Trim(), trimmedRouteId, StringComparison.OrdinalIgnoreCase))
      {
        AddRouteAliases(allowedRouteIds, route.RouteId);
        AddRouteAliases(allowedRouteIds, route.RouteShortName);
        AddRouteAliases(allowedRouteIds, route.RouteLongName);
      }
    }

    return FilterVehiclesByRouteIdentifiers(liveVehicles, allowedRouteIds, providerName);
  }

  private static List<BusLocation> FilterVehiclesByRouteIdentifiers(
    IReadOnlyList<BusLocation> liveVehicles,
    HashSet<string> allowedRouteIds,
    string? requiredProviderName = null)
  {
    if (allowedRouteIds.Count == 0 || liveVehicles.Count == 0)
    {
      return [];
    }

    var trimmedProvider = string.IsNullOrWhiteSpace(requiredProviderName)
      ? null
      : requiredProviderName.Trim();

    var filtered = new List<BusLocation>();
    foreach (var vehicle in liveVehicles)
    {
      if (trimmedProvider is not null &&
          !string.Equals(vehicle.ProviderName?.Trim(), trimmedProvider, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (MatchesAnyRouteIdentifier(vehicle.RouteId, allowedRouteIds) ||
          MatchesAnyRouteIdentifier(vehicle.RouteShortName, allowedRouteIds))
      {
        filtered.Add(vehicle);
      }
    }

    return filtered;
  }

  private static List<BusLocation>? FindMatchingVehicles(
    Dictionary<string, List<BusLocation>> vehiclesByRoute,
    TransitStep step) =>
    FindMatchingVehiclesByName(vehiclesByRoute, step.LineShortName)
    ?? FindMatchingVehiclesByName(vehiclesByRoute, step.LineName);

  private static List<BusLocation>? FindMatchingVehiclesByName(
    Dictionary<string, List<BusLocation>> vehiclesByRoute,
    string? rawName)
  {
    if (string.IsNullOrWhiteSpace(rawName))
    {
      return null;
    }

    var normalized = NormalizeRouteName(rawName);
    if (normalized.Length > 0 && vehiclesByRoute.TryGetValue(normalized, out var direct))
    {
      return direct;
    }

    var withoutSuffix = StripTrailingRouteSuffix(rawName);
    return withoutSuffix is not null && vehiclesByRoute.TryGetValue(withoutSuffix, out var stripped)
      ? stripped
      : null;
  }

  private static HashSet<string> CollectRouteIdentifiers(GoogleRoute route)
  {
    var allowedRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var segment in route.Segments)
    {
      if (string.Equals(segment.TravelMode, "WALKING", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      AddRouteAliases(allowedRouteIds, segment.LineShortName);
      AddRouteAliases(allowedRouteIds, segment.LineName);
    }

    foreach (var step in route.TransitSteps)
    {
      AddRouteAliases(allowedRouteIds, step.LineShortName);
      AddRouteAliases(allowedRouteIds, step.LineName);
    }

    return allowedRouteIds;
  }

  private static readonly string[] TrailingRouteSuffixes = [" line", " inbound", " outbound"];

  private static void AddRouteAliases(HashSet<string> target, string? rawName)
  {
    if (string.IsNullOrWhiteSpace(rawName))
    {
      return;
    }

    var normalized = NormalizeRouteName(rawName);
    if (normalized.Length > 0)
    {
      target.Add(normalized);
    }

    var withoutSuffix = StripTrailingRouteSuffix(rawName);
    if (withoutSuffix is not null)
    {
      target.Add(withoutSuffix);
    }
  }

  private static bool MatchesAnyRouteIdentifier(string? vehicleRouteId, HashSet<string> allowedRouteIds)
  {
    if (string.IsNullOrWhiteSpace(vehicleRouteId))
    {
      return false;
    }

    var normalized = NormalizeRouteName(vehicleRouteId);
    if (normalized.Length > 0 && allowedRouteIds.Contains(normalized))
    {
      return true;
    }

    var withoutSuffix = StripTrailingRouteSuffix(vehicleRouteId);
    return withoutSuffix is not null && allowedRouteIds.Contains(withoutSuffix);
  }

  private static string? StripTrailingRouteSuffix(string rawName)
  {
    var trimmed = rawName.AsSpan().Trim();
    foreach (var suffix in TrailingRouteSuffixes)
    {
      if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      var prefix = trimmed[..^suffix.Length].Trim();
      var normalizedPrefix = NormalizeRouteName(prefix);
      if (normalizedPrefix.Length == 0)
      {
        return null;
      }

      return normalizedPrefix;
    }

    return null;
  }

  private static Dictionary<string, List<BusLocation>> IndexVehiclesByRoute(List<BusLocation> liveVehicles)
  {
    var vehiclesByRoute = new Dictionary<string, List<BusLocation>>(StringComparer.OrdinalIgnoreCase);
    var normalizedRouteNames = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var vehicle in liveVehicles)
    {
      var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      IndexVehicleRouteKey(vehiclesByRoute, normalizedRouteNames, addedKeys, vehicle.RouteId, vehicle);
      IndexVehicleRouteKey(vehiclesByRoute, normalizedRouteNames, addedKeys, vehicle.RouteShortName, vehicle);
    }

    return vehiclesByRoute;
  }

  private static void IndexVehicleRouteKey(
    Dictionary<string, List<BusLocation>> vehiclesByRoute,
    Dictionary<string, string> normalizedRouteNames,
    HashSet<string> addedKeys,
    string? rawRouteName,
    BusLocation vehicle)
  {
    var normalized = GetNormalizedRouteName(rawRouteName, normalizedRouteNames);
    if (normalized.Length > 0 && addedKeys.Add(normalized))
    {
      AddVehicleToBucket(vehiclesByRoute, normalized, vehicle);
    }

    if (!string.IsNullOrWhiteSpace(rawRouteName))
    {
      var withoutSuffix = StripTrailingRouteSuffix(rawRouteName);
      if (withoutSuffix is not null && addedKeys.Add(withoutSuffix))
      {
        AddVehicleToBucket(vehiclesByRoute, withoutSuffix, vehicle);
      }
    }
  }

  private static string GetNormalizedRouteName(
    string? routeName,
    Dictionary<string, string> normalizedRouteNames)
  {
    if (string.IsNullOrWhiteSpace(routeName))
    {
      return string.Empty;
    }

    if (!normalizedRouteNames.TryGetValue(routeName, out var normalizedRouteName))
    {
      normalizedRouteName = NormalizeRouteName(routeName);
      normalizedRouteNames.Add(routeName, normalizedRouteName);
    }

    return normalizedRouteName;
  }

  private static void AddVehicleToBucket(
    Dictionary<string, List<BusLocation>> vehiclesByRoute,
    string normalizedRouteName,
    BusLocation vehicle)
  {
    if (!vehiclesByRoute.TryGetValue(normalizedRouteName, out var routeVehicles))
    {
      routeVehicles = new List<BusLocation>();
      vehiclesByRoute.Add(normalizedRouteName, routeVehicles);
    }

    routeVehicles.Add(vehicle);
  }

  private static string NormalizeRouteName(string routeName) =>
    NormalizeRouteName(routeName.AsSpan(), routeName);

  private static string NormalizeRouteName(ReadOnlySpan<char> routeName, string? original = null)
  {
    var normalizedLength = 0;
    var isAlreadyNormalized = true;

    foreach (var character in routeName)
    {
      if (char.IsLetterOrDigit(character))
      {
        normalizedLength++;
      }
      else
      {
        isAlreadyNormalized = false;
      }
    }

    if (normalizedLength == 0)
    {
      return string.Empty;
    }

    if (isAlreadyNormalized)
    {
      return original is not null && original.Length == routeName.Length
        ? original
        : new string(routeName);
    }

    return string.Create(normalizedLength, routeName, static (destination, source) =>
    {
      var destinationIndex = 0;
      foreach (var character in source)
      {
        if (char.IsLetterOrDigit(character))
        {
          destination[destinationIndex++] = character;
        }
      }
    });
  }
}
