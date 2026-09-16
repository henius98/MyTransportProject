using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces
{
  public enum GtfsFetchStatus
  {
    Failed,
    Updated,
    NotModified
  }

  public readonly record struct GtfsFetchResult(
    GtfsFetchStatus Status,
    List<BusLocation>? Vehicles = null);

  public interface IGtfsService
  {
    Task<GtfsFetchResult> GetBusPositionsAsync(
      string url,
      bool allowNotModified,
      CancellationToken cancellationToken = default);
  }
}
