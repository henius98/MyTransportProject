using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MyTransportAppWASM.Features.Google.Services
{
  /// <summary>
  /// Unified place resolution service: resolves coordinate inputs,
  /// searches the weather station catalog, and queries Google Geocoding via JS.
  /// </summary>
  public sealed class PlaceResolutionService : IPlaceResolutionService, IAsyncDisposable
  {
    private readonly IWeatherStationCatalogService _stationCatalog;
    private readonly JsModuleLoader? _mapModule;
    private readonly ILogger<PlaceResolutionService> _logger;
    private readonly string _apiKey;

    public PlaceResolutionService(
      IConfiguration configuration,
      IWeatherStationCatalogService stationCatalog,
      IJSRuntime? jsRuntime = null,
      ILogger<PlaceResolutionService>? logger = null)
    {
      _stationCatalog = stationCatalog;
      _apiKey = configuration["GoogleMaps:ApiKey"] ?? string.Empty;
      _mapModule = jsRuntime is null ? null : new JsModuleLoader(jsRuntime, "./js/googleMap.js", passCancellationToken: true);
      _logger = logger ?? NullLogger<PlaceResolutionService>.Instance;
    }

    public async Task<(double Lat, double Lng, string FormattedAddress)?> GeocodeAsync(string address, CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();

      if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(_apiKey))
        return null;

      // Geocode address via Google Maps JavaScript Geocoder to avoid browser CORS restrictions
      if (_mapModule != null)
      {
        try
        {
          var module = await _mapModule.TryGetAsync(cancellationToken);
          if (module != null)
          {
            var place = await module.InvokeAsync<GeocodeResult?>("geocodePlaceName", cancellationToken, address, _apiKey);
            if (place != null && GeoMath.IsValidCoordinates(place.Lat, place.Lng))
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

    public async Task<List<PlaceSuggestion>> GetSuggestionsAsync(string address, CancellationToken cancellationToken = default)
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
        if (!GeoMath.IsValidCoordinates(latitude, longitude))
          return new();

        return [new PlaceSuggestion(latitude, longitude, "Latitude, longitude", FormattableString.Invariant($"{latitude}, {longitude}"))];
      }

      try
      {
        var stations = await _stationCatalog.SearchStationsAsync(address, cancellationToken: cancellationToken);
        if (stations.Count > 0)
        {
          return stations
            .Select(l => new PlaceSuggestion(
              l.Latitude,
              l.Longitude,
              l.LocationName ?? l.LocationId ?? string.Empty,
              l.LocationName ?? l.LocationId!,
              l.LocationId))
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
          return [new PlaceSuggestion(place.Value.Lat, place.Value.Lng, place.Value.FormattedAddress, address)];
        }
      }

      return new();
    }

    public async Task<string?> ReverseGeocodeAsync(double lat, double lng, CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();

      if (string.IsNullOrWhiteSpace(_apiKey) || !GeoMath.IsValidCoordinates(lat, lng))
      {
        return null;
      }

      if (_mapModule != null)
      {
        try
        {
          var module = await _mapModule.TryGetAsync(cancellationToken);
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

    public ValueTask DisposeAsync() => _mapModule?.DisposeAsync() ?? ValueTask.CompletedTask;
  }
}

