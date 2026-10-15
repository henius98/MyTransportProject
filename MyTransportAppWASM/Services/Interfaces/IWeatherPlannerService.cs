
namespace MyTransportAppWASM.Services.Interfaces
{
  public interface IWeatherPlannerService
  {
    Task<WeatherPlanResult> GetPlanAsync(
      WeatherQuery query,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false);

    Task<WeatherProviderResult> RefreshProviderAsync(
      string providerName,
      WeatherQuery query,
      bool isOutlook,
      CancellationToken cancellationToken = default);

    Task<WeatherProviderResult> GetHourlyForecastAsync(
      WeatherQuery query,
      DateTime date,
      CancellationToken cancellationToken = default,
      bool forceRefresh = false);

    Task ResolveNearestLocationAsync(
      WeatherQuery query,
      bool updatePlaceName = false,
      CancellationToken cancellationToken = default);
  }
}
