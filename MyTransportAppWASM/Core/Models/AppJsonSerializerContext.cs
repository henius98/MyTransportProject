using System.Text.Json.Serialization;

namespace MyTransportAppWASM.Core.Models
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
  [JsonSerializable(typeof(GeocodeResult))]
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
  [JsonSerializable(typeof(Dictionary<string, string>))]
  internal partial class AppJsonSerializerContext : JsonSerializerContext
  {
  }
}
