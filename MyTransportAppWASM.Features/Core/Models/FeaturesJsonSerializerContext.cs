using System.Text.Json.Serialization;
using MyTransportAppWASM.Features.Fortune.Models;

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
  [JsonSerializable(typeof(CachedDateFortune))]
  internal partial class FeaturesJsonSerializerContext : JsonSerializerContext
  {
  }
}
