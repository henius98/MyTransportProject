using System.Text.Json.Serialization;
using static MyTransportAppWASM.Services.GeocodingService;

namespace MyTransportAppWASM.Models
{
  [JsonSourceGenerationOptions(
      WriteIndented = false,
      PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
  [JsonSerializable(typeof(WeatherQuery))]
  [JsonSerializable(typeof(WeatherTimeSlice))]
  [JsonSerializable(typeof(WeatherProviderResult))]
  [JsonSerializable(typeof(WeatherPlanResult))]
  [JsonSerializable(typeof(WeatherOptions))]
  [JsonSerializable(typeof(WeatherProviderOptions))]
  [JsonSerializable(typeof(GeocodingRequest))]
  [JsonSerializable(typeof(PlacesNewResponse))]
  [JsonSerializable(typeof(List<MetLocation>))]
  internal partial class AppJsonSerializerContext : JsonSerializerContext
  {
  }
}
