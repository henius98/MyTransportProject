using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services;

public enum ProviderVehicleRefreshStatus
{
  Failed,
  Updated,
  NotModified
}

public readonly record struct ProviderVehicleRefresh(
  string Endpoint,
  ProviderVehicleRefreshStatus Status,
  List<BusLocation>? Vehicles = null);

public readonly record struct TransitRefreshOutcome(
  List<BusLocation> Vehicles,
  int AttemptedProviderCount,
  int DeferredProviderCount,
  int FailedProviderCount,
  int StaleProviderCount,
  int ExpiredProviderCount);

/// <summary>
/// Coordinates provider refresh schedules and retains only recently validated vehicle snapshots.
/// The class is deliberately independent of HTTP and UI code so backoff and expiry behavior can
/// be verified deterministically.
/// </summary>
public sealed class TransitRefreshCoordinator
{
  private readonly TimeSpan _normalInterval;
  private readonly TimeSpan _maximumBackoff;
  private readonly TimeSpan _maximumSnapshotAge;
  private Dictionary<string, ProviderState> _states = new(StringComparer.Ordinal);

  public TransitRefreshCoordinator(
    TimeSpan normalInterval,
    TimeSpan maximumBackoff,
    TimeSpan maximumSnapshotAge)
  {
    if (normalInterval <= TimeSpan.Zero)
      throw new ArgumentOutOfRangeException(nameof(normalInterval));
    if (maximumBackoff < normalInterval)
      throw new ArgumentOutOfRangeException(nameof(maximumBackoff));
    if (maximumSnapshotAge < normalInterval)
      throw new ArgumentOutOfRangeException(nameof(maximumSnapshotAge));

    _normalInterval = normalInterval;
    _maximumBackoff = maximumBackoff;
    _maximumSnapshotAge = maximumSnapshotAge;
  }

  public List<TransportProvider> GetProvidersToRefresh(
    IReadOnlyList<TransportProvider> nearbyProviders,
    DateTimeOffset now,
    bool force)
  {
    var dueProviders = new List<TransportProvider>(nearbyProviders.Count);
    foreach (var provider in nearbyProviders)
    {
      if (force ||
          !_states.TryGetValue(provider.Endpoint, out var state) ||
          now >= state.NextAttemptAt)
      {
        dueProviders.Add(provider);
      }
    }

    return dueProviders;
  }

  public bool HasUsableSnapshot(string endpoint, DateTimeOffset now) =>
    _states.TryGetValue(endpoint, out var state) &&
    state.Vehicles is not null &&
    now - state.LastValidatedAt <= _maximumSnapshotAge;

  public TransitRefreshOutcome ApplyResults(
    IReadOnlyList<TransportProvider> nearbyProviders,
    IReadOnlyList<ProviderVehicleRefresh> refreshes,
    DateTimeOffset now)
  {
    var activeEndpoints = new HashSet<string>(StringComparer.Ordinal);
    foreach (var provider in nearbyProviders)
    {
      activeEndpoints.Add(provider.Endpoint);
    }

    RemoveInactiveProviders(activeEndpoints);

    var refreshByEndpoint = new Dictionary<string, ProviderVehicleRefresh>(
      refreshes.Count,
      StringComparer.Ordinal);
    foreach (var refresh in refreshes)
    {
      refreshByEndpoint[refresh.Endpoint] = refresh;
    }

    var failedProviderCount = 0;
    var expiredProviderCount = 0;

    foreach (var provider in nearbyProviders)
    {
      if (!_states.TryGetValue(provider.Endpoint, out var state))
      {
        state = new ProviderState();
        _states.Add(provider.Endpoint, state);
      }

      if (refreshByEndpoint.TryGetValue(provider.Endpoint, out var refresh))
      {
        switch (refresh.Status)
        {
          case ProviderVehicleRefreshStatus.Updated when refresh.Vehicles is not null:
            state.Vehicles = refresh.Vehicles;
            MarkValidated(state, now);
            break;

          case ProviderVehicleRefreshStatus.NotModified when state.Vehicles is not null:
            MarkValidated(state, now);
            break;

          default:
            failedProviderCount++;
            MarkFailure(state, now);
            break;
        }
      }

      if (state.Vehicles is not null && now - state.LastValidatedAt > _maximumSnapshotAge)
      {
        state.Vehicles = null;
        expiredProviderCount++;
      }
    }

    var totalVehicles = 0;
    var staleProviderCount = 0;
    foreach (var provider in nearbyProviders)
    {
      var state = _states[provider.Endpoint];
      if (state.Vehicles is null) continue;
      totalVehicles += state.Vehicles.Count;
      if (state.ConsecutiveFailures > 0) staleProviderCount++;
    }

    var vehicles = new List<BusLocation>(totalVehicles);
    foreach (var provider in nearbyProviders)
    {
      var state = _states[provider.Endpoint];
      if (state.Vehicles is not null)
      {
        vehicles.AddRange(state.Vehicles);
      }
    }

    return new TransitRefreshOutcome(
      vehicles,
      refreshes.Count,
      nearbyProviders.Count - refreshes.Count,
      failedProviderCount,
      staleProviderCount,
      expiredProviderCount);
  }

  public void Clear() => _states.Clear();

  private void MarkValidated(ProviderState state, DateTimeOffset now)
  {
    state.LastValidatedAt = now;
    state.NextAttemptAt = now + _normalInterval;
    state.ConsecutiveFailures = 0;
  }

  private void MarkFailure(ProviderState state, DateTimeOffset now)
  {
    state.ConsecutiveFailures = Math.Min(state.ConsecutiveFailures + 1, 30);
    var shift = Math.Min(state.ConsecutiveFailures, 20);
    var multiplier = 1L << shift;
    var backoffTicks = _normalInterval.Ticks > _maximumBackoff.Ticks / multiplier
      ? _maximumBackoff.Ticks
      : _normalInterval.Ticks * multiplier;
    state.NextAttemptAt = now + TimeSpan.FromTicks(backoffTicks);
  }

  private void RemoveInactiveProviders(HashSet<string> activeEndpoints)
  {
    List<string>? endpointsToRemove = null;
    foreach (var endpoint in _states.Keys)
    {
      if (!activeEndpoints.Contains(endpoint))
      {
        (endpointsToRemove ??= []).Add(endpoint);
      }
    }

    if (endpointsToRemove is null) return;
    foreach (var endpoint in endpointsToRemove)
    {
      _states.Remove(endpoint);
    }
  }

  private sealed class ProviderState
  {
    public List<BusLocation>? Vehicles { get; set; }
    public DateTimeOffset LastValidatedAt { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.MinValue;
    public int ConsecutiveFailures { get; set; }
  }
}
