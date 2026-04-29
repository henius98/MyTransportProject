using MyTransportAppWASM.Models.Weather;

namespace MyTransportAppWASM.Services.Interfaces
{
  public interface IWeatherPlannerService
  {
    Task<WeatherPlanResult> GetWeatherPlanAsync(WeatherQuery query, CancellationToken cancellationToken = default);
  }
}
