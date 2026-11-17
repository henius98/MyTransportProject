using System.Text.Json.Serialization;

namespace MyTransportAppWASM.Core.Models
{
  [JsonSourceGenerationOptions(
      WriteIndented = false,
      PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
  [JsonSerializable(typeof(List<MetLocation>))]
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
