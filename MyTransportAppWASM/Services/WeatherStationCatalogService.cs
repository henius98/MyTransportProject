using System.Net.Http.Json;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Services
{
  /// <summary>
  /// Manages loading, caching, and spatial querying of the MetMalaysia recreation centres catalog.
  /// Decouples static station assets from dynamic geocoding services.
  /// </summary>
  public class WeatherStationCatalogService : IWeatherStationCatalogService
  {
    private readonly HttpClient _httpClient;
    private Task<List<MetLocation>?>? _stationsTask;

    public WeatherStationCatalogService(HttpClient httpClient)
    {
      _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<MetLocation>> GetAllStationsAsync(CancellationToken cancellationToken = default)
    {
      try
      {
        var stations = await GetStationsInternalAsync(cancellationToken);
        return stations ?? (IReadOnlyList<MetLocation>)Array.Empty<MetLocation>();
      }
      catch (OperationCanceledException)
      {
        return Array.Empty<MetLocation>();
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Failed to load MetMalaysia locations: {ex.Message}");
        return Array.Empty<MetLocation>();
      }
    }

    public async Task<MetLocation?> FindNearestStationAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
      var stations = await GetAllStationsAsync(cancellationToken);
      return stations
        .Where(s => !string.IsNullOrWhiteSpace(s.LocationId))
        .MinBy(s => GeoMath.DistanceKilometers(latitude, longitude, s.Latitude, s.Longitude));
    }

    public async Task<IReadOnlyList<MetLocation>> SearchStationsAsync(string query, int maxResults = 15, CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrWhiteSpace(query))
        return Array.Empty<MetLocation>();

      query = query.Trim();
      var stations = await GetAllStationsAsync(cancellationToken);
      return stations
        .Where(s => s.LocationName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                 || s.LocationId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        .OrderByDescending(s => string.Equals(s.LocationId, query, StringComparison.OrdinalIgnoreCase))
        .Take(maxResults)
        .ToList();
    }

    private async Task<List<MetLocation>?> GetStationsInternalAsync(CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();

      var loadTask = _stationsTask ??= _httpClient.GetFromJsonAsync(
        "data/recreation_centres.json",
        AppJsonSerializerContext.Default.ListMetLocation,
        CancellationToken.None);

      try
      {
        return await loadTask.WaitAsync(cancellationToken);
      }
      catch (OperationCanceledException)
      {
        // Individual caller cancelled while waiting; do not invalidate the shared asset loading task.
        throw;
      }
      catch
      {
        // Allow a later call to retry after a transient static-asset failure.
        if (ReferenceEquals(_stationsTask, loadTask))
        {
          _stationsTask = null;
        }

        throw;
      }
    }
  }
}
