using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services
{
  /// <summary>
  /// Unified place resolution service: resolves coordinate inputs,
  /// searches the weather station catalog, and queries Google Geocoding via JS.
  /// </summary>
  public sealed class PlaceResolutionService : IPlaceResolutionService, IAsyncDisposable
  {
    private readonly IWeatherStationCatalogService _stationCatalog;
    private readonly IJSRuntime? _jsRuntime;
    private readonly ILogger<PlaceResolutionService> _logger;
    private readonly string _apiKey;
    private readonly object _lock = new();
    private Task<IJSObjectReference>? _moduleTask;
    private bool _disposed;

    public PlaceResolutionService(
      IConfiguration configuration,
      IWeatherStationCatalogService stationCatalog,
      IJSRuntime? jsRuntime = null,
      ILogger<PlaceResolutionService>? logger = null)
    {
      _stationCatalog = stationCatalog;
      _apiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
      _jsRuntime = jsRuntime;
      _logger = logger ?? NullLogger<PlaceResolutionService>.Instance;
    }

    private async ValueTask<IJSObjectReference?> GetMapModuleAsync(CancellationToken cancellationToken = default)
    {
      if (_disposed || _jsRuntime == null)
        return null;

      Task<IJSObjectReference> moduleTask;
      lock (_lock)
      {
        if (_disposed) return null;
        if (_moduleTask == null || _moduleTask.IsFaulted || _moduleTask.IsCanceled)
        {
          _moduleTask = _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken: CancellationToken.None, ["./js/googleMap.js"]).AsTask();
        }
        moduleTask = _moduleTask;
      }

      return await moduleTask.WaitAsync(cancellationToken);
    }

    public async Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address, CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();

      if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(_apiKey))
        return null;

      // Geocode address via Google Maps JavaScript Geocoder to avoid browser CORS restrictions
      if (_jsRuntime != null)
      {
        try
        {
          var module = await GetMapModuleAsync(cancellationToken);
          if (module != null)
          {
            var place = await module.InvokeAsync<GeocodeResult?>("geocodePlaceName", cancellationToken, address, _apiKey);
            if (place != null &&
                double.IsFinite(place.Lat) && double.IsFinite(place.Lng) &&
                place.Lat is >= -90 and <= 90 && place.Lng is >= -180 and <= 180)
            {
              return (place.Lat, place.Lng, place.FormattedAddress ?? string.Empty);
            }
          }
        }
        catch (OperationCanceledException)
        {
          throw;
        }
        catch (Exception ex)
        {
          _logger.LogDebug("Geocoding via JS failed: {Message}", ex.Message);
        }
      }

      return null;
    }

    public async Task<List<(double Lat, double Lng, string FormattedAddress, string Name)>> GetSuggestionsAsync(string address, CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrWhiteSpace(address))
        return new();

      address = address.Trim();
      var coordinates = address.Contains(',')
        ? address.Split(',', StringSplitOptions.TrimEntries)
        : address.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (coordinates.Length == 2
        && double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
        && double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
      {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)
          || latitude is < -90 or > 90 || longitude is < -180 or > 180)
          return new();

        return [(latitude, longitude, "Latitude, longitude", FormattableString.Invariant($"{latitude}, {longitude}"))];
      }

      try
      {
        var stations = await _stationCatalog.SearchStationsAsync(address, cancellationToken: cancellationToken);
        if (stations.Count > 0)
        {
          return stations
            .Select(l => (l.Latitude, l.Longitude, $"MET ID: {l.LocationId}", l.LocationName ?? l.LocationId!))
            .ToList();
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogDebug("Failed to query weather stations for suggestions: {Message}", ex.Message);
      }

      // If no static station matched and API key is present, resolve real place via Google Geocoding
      if (!string.IsNullOrWhiteSpace(_apiKey))
      {
        var place = await GeocodeAsync(address, cancellationToken);
        if (place != null)
        {
          return [(place.Value.Lat, place.Value.Lng, place.Value.FormattedAddress, address)];
        }
      }

      return new();
    }

    public async Task<string?> ReverseGeocodeAsync(double lat, double lng, CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();

      if (string.IsNullOrWhiteSpace(_apiKey) ||
          !double.IsFinite(lat) || !double.IsFinite(lng) ||
          lat is < -90 or > 90 || lng is < -180 or > 180)
      {
        return null;
      }

      if (_jsRuntime != null)
      {
        try
        {
          var module = await GetMapModuleAsync(cancellationToken);
          if (module != null)
          {
            return await module.InvokeAsync<string?>("reverseGeocode", cancellationToken, lat, lng, _apiKey);
          }
        }
        catch (OperationCanceledException)
        {
          throw;
        }
        catch (Exception ex)
        {
          _logger.LogDebug("Reverse Geocoding via JS failed: {Message}", ex.Message);
        }
      }

      return null;
    }

    public async ValueTask DisposeAsync()
    {
      Task<IJSObjectReference>? moduleTask;
      lock (_lock)
      {
        if (_disposed) return;
        _disposed = true;
        moduleTask = _moduleTask;
        _moduleTask = null;
      }

      if (moduleTask != null)
      {
        try
        {
          var module = await moduleTask;
          await module.DisposeAsync();
        }
        catch (Exception)
        {
        }
      }
    }
  }
}

