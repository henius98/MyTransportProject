using System.Text.Json.Serialization;

namespace MyTransportAppWASM.Core.Auth;

public sealed class FirebaseOptions
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("authDomain")]
    public string? AuthDomain { get; set; }

    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("storageBucket")]
    public string? StorageBucket { get; set; }

    [JsonPropertyName("messagingSenderId")]
    public string? MessagingSenderId { get; set; }

    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    [JsonPropertyName("measurementId")]
    public string? MeasurementId { get; set; }

    [JsonPropertyName("googleClientId")]
    public string? GoogleClientId { get; set; }

    [JsonPropertyName("webPushVapidKey")]
    public string? WebPushVapidKey { get; set; }
}
