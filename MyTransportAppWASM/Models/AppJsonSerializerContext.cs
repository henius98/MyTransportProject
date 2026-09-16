using System.Text.Json.Serialization;
using static MyTransportAppWASM.Services.GeocodingService;
using MyTransportAppWASM.Models.BaziFlow;

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
  [JsonSerializable(typeof(GoogleRoute))]
  [JsonSerializable(typeof(List<GoogleRoute>))]
  [JsonSerializable(typeof(BusLocation))]
  [JsonSerializable(typeof(List<BusLocation>))]
  [JsonSerializable(typeof(EnrichedRoute))]
  [JsonSerializable(typeof(List<EnrichedRoute>))]
  [JsonSerializable(typeof(ApiResponse<ProfileData>))]
  [JsonSerializable(typeof(ApiResponse<FortuneData>))]
  [JsonSerializable(typeof(CreateProfileRequest))]
  [JsonSerializable(typeof(DateFortuneRequest))]
  [JsonSerializable(typeof(UserSettings))]
  internal partial class AppJsonSerializerContext : JsonSerializerContext
  {
  }
}
