namespace MyTransportAppWASM.Features.Transit.Models;

public sealed class TransitPollingOptions
{
  public const string SectionName = "TransitPolling";

  public int IntervalSeconds { get; set; } = 15;
  public int MaximumBackoffSeconds { get; set; } = 120;
  public int MaximumSnapshotAgeSeconds { get; set; } = 120;
}

public sealed class TransitOptions
{
  public IReadOnlyList<TransportProvider> Providers { get; init; } = [];
  public TransitPollingOptions Polling { get; init; } = new();
}
