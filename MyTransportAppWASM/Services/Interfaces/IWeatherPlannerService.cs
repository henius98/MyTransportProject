
namespace MyTransportAppWASM.Services.Interfaces
{
  public interface IWeatherPlannerService
  {
    Task<WeatherProviderResult> FetchProviderDataAsync(WeatherProviderOptions provider, Uri endpoint, string label, CancellationToken cancellationToken = default);
  }
}
