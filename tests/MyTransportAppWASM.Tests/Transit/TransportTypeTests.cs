using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Tests;

public sealed class TransportTypeTests
{
  [Fact]
  public void ProviderTransportType_BindsFromConfiguration()
  {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["TransportProviders:0:Name"] = "KTMB National Rail",
      ["TransportProviders:0:Endpoint"] = "https://example.test/ktmb",
      ["TransportProviders:0:TransportType"] = "KTMB",
      ["TransportProviders:0:CenterLat"] = "3.1344",
      ["TransportProviders:0:CenterLng"] = "101.6865",
      ["TransportProviders:0:RadiusKm"] = "500"
    }).Build();

    var provider = Assert.Single(configuration.GetSection("TransportProviders").Get<List<TransportProvider>>()!);

    Assert.Equal("KTMB", provider.TransportType);
  }

  [Fact]
  public void VehicleTransportType_SerializesForMapInterop()
  {
    var json = JsonSerializer.Serialize(new BusLocation { TransportType = "KTMB" });

    Assert.Equal("KTMB", JsonDocument.Parse(json).RootElement.GetProperty("transportType").GetString());
  }
}
