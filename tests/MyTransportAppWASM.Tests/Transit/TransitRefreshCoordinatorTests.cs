using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public sealed class TransitRefreshCoordinatorTests
{
  private static readonly DateTimeOffset Start = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
  private static readonly TransportProvider ProviderA = new("A", "https://a.test/feed", 0, 0, 1);
  private static readonly TransportProvider ProviderB = new("B", "https://b.test/feed", 0, 0, 1);

  [Fact]
  public void Failure_RetainsRecentSnapshotAndAppliesExponentialBackoff()
  {
    var coordinator = CreateCoordinator();
    coordinator.ApplyResults(
      [ProviderA],
      [Updated(ProviderA, "vehicle-a")],
      Start);

    var failure = coordinator.ApplyResults(
      [ProviderA],
      [Failed(ProviderA)],
      Start.AddSeconds(15));

    Assert.Single(failure.Vehicles);
    Assert.Equal(1, failure.StaleProviderCount);
    Assert.Empty(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(44), force: false));
    Assert.Single(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(45), force: false));
  }

  [Fact]
  public void Snapshot_IsRemovedAfterMaximumStaleness()
  {
    var coordinator = CreateCoordinator();
    coordinator.ApplyResults(
      [ProviderA],
      [Updated(ProviderA, "vehicle-a")],
      Start);
    coordinator.ApplyResults(
      [ProviderA],
      [Failed(ProviderA)],
      Start.AddSeconds(15));

    var expired = coordinator.ApplyResults(
      [ProviderA],
      [],
      Start.AddSeconds(121));

    Assert.Empty(expired.Vehicles);
    Assert.Equal(1, expired.ExpiredProviderCount);
    Assert.False(coordinator.HasUsableSnapshot(ProviderA.Endpoint, Start.AddSeconds(121)));
  }

  [Fact]
  public void NotModified_RevalidatesSnapshotAndResetsFailureBackoff()
  {
    var coordinator = CreateCoordinator();
    coordinator.ApplyResults(
      [ProviderA],
      [Updated(ProviderA, "vehicle-a")],
      Start);
    coordinator.ApplyResults(
      [ProviderA],
      [Failed(ProviderA)],
      Start.AddSeconds(15));

    var revalidated = coordinator.ApplyResults(
      [ProviderA],
      [new ProviderVehicleRefresh(ProviderA.Endpoint, ProviderVehicleRefreshStatus.NotModified)],
      Start.AddSeconds(45));

    Assert.Single(revalidated.Vehicles);
    Assert.Equal(0, revalidated.StaleProviderCount);
    Assert.Empty(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(59), force: false));
    Assert.Single(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(60), force: false));
    Assert.True(coordinator.HasUsableSnapshot(ProviderA.Endpoint, Start.AddSeconds(165)));
  }

  [Fact]
  public void RepeatedFailures_CapAtConfiguredMaximumBackoff()
  {
    var coordinator = CreateCoordinator();
    coordinator.ApplyResults([ProviderA], [Failed(ProviderA)], Start);
    Assert.Single(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(30), force: false));

    coordinator.ApplyResults([ProviderA], [Failed(ProviderA)], Start.AddSeconds(30));
    Assert.Empty(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(89), force: false));
    Assert.Single(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(90), force: false));

    coordinator.ApplyResults([ProviderA], [Failed(ProviderA)], Start.AddSeconds(90));
    Assert.Empty(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(209), force: false));
    Assert.Single(coordinator.GetProvidersToRefresh([ProviderA], Start.AddSeconds(210), force: false));
  }

  [Fact]
  public void LeavingProviderArea_DiscardsItsSnapshot()
  {
    var coordinator = CreateCoordinator();
    coordinator.ApplyResults(
      [ProviderA, ProviderB],
      [Updated(ProviderA, "vehicle-a"), Updated(ProviderB, "vehicle-b")],
      Start);

    var remaining = coordinator.ApplyResults([ProviderA], [], Start.AddSeconds(15));

    Assert.Single(remaining.Vehicles);
    Assert.Equal("vehicle-a", remaining.Vehicles[0].VehicleId);
    Assert.False(coordinator.HasUsableSnapshot(ProviderB.Endpoint, Start.AddSeconds(15)));
  }

  private static TransitRefreshCoordinator CreateCoordinator() => new(
    TimeSpan.FromSeconds(15),
    TimeSpan.FromSeconds(120),
    TimeSpan.FromSeconds(120));

  private static ProviderVehicleRefresh Updated(TransportProvider provider, string vehicleId) =>
    new(
      provider.Endpoint,
      ProviderVehicleRefreshStatus.Updated,
      [new BusLocation { VehicleId = vehicleId }]);

  private static ProviderVehicleRefresh Failed(TransportProvider provider) =>
    new(provider.Endpoint, ProviderVehicleRefreshStatus.Failed);
}
