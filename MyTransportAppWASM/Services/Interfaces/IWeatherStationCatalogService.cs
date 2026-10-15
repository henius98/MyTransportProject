using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces
{
  public interface IWeatherStationCatalogService
  {
    /// <summary>
    /// Retrieves all weather forecast stations/centres from the static catalog.
    /// </summary>
    Task<IReadOnlyList<MetLocation>> GetAllStationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the nearest weather station to the given coordinates.
    /// </summary>
    Task<MetLocation?> FindNearestStationAsync(double latitude, double longitude, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the static station catalog by station name or ID.
    /// </summary>
    Task<IReadOnlyList<MetLocation>> SearchStationsAsync(string query, int maxResults = 15, CancellationToken cancellationToken = default);
  }
}
