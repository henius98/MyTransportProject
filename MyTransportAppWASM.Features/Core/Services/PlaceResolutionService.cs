using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MyTransportAppWASM.Core.Services
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
      var place = await InvokeMapModuleAsync<GeocodeResult>("geocodePlaceName", "Geocoding", cancellationToken, address, _apiKey);
      if (place != null && GeoMath.IsValidCoordinates(place.Lat, place.Lng))
      {
        return (place.Lat, place.Lng, place.FormattedAddress ?? string.Empty);
      }

      return null;
    }

    public async Task<List<PlaceSuggestion>> GetSuggestionsAsync(string address, CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrWhiteSpace(address))
        return new();

      address = address.Trim();
      if (TryParseCoordinatePair(address, out var latitude, out var longitude))
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

      return await InvokeMapModuleAsync<string>("reverseGeocode", "Reverse Geocoding", cancellationToken, lat, lng, _apiKey);
    }

    private async Task<T?> InvokeMapModuleAsync<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
      string identifier,
      string failureAction,
      CancellationToken cancellationToken,
      params object?[] args)
    {
      if (_mapModule is null) return default;

      try
      {
        var module = await _mapModule.TryGetAsync(cancellationToken);
        if (module is not null)
        {
          return await module.InvokeAsync<T?>(identifier, cancellationToken, args);
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogDebug("{Action} via JS failed: {Message}", failureAction, ex.Message);
      }

      return default;
    }

    private static bool TryParseCoordinatePair(ReadOnlySpan<char> input, out double latitude, out double longitude)
    {
      latitude = 0;
      longitude = 0;
      Span<Range> ranges = stackalloc Range[3];
      int count = input.Contains(',')
        ? input.Split(ranges, ',', StringSplitOptions.TrimEntries)
        : input.SplitAny(ranges, " \t\r\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

      return count == 2
        && double.TryParse(input[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
        && double.TryParse(input[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out longitude);
    }

    public ValueTask DisposeAsync() => _mapModule?.DisposeAsync() ?? ValueTask.CompletedTask;
  }
}

