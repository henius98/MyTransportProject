using System.Text.Json.Serialization;

namespace MyTransportAppWASM.Core.Models
{
  [JsonSourceGenerationOptions(
      WriteIndented = false,
      PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
  [JsonSerializable(typeof(UserSettings))]
  [JsonSerializable(typeof(Dictionary<string, string>))]
  internal partial class AppJsonSerializerContext : JsonSerializerContext
  {
  }
}
