namespace MyTransportAppWASM.Core.Models;

public sealed record PlaceSuggestion(
  double Lat,
  double Lng,
  string FormattedAddress,
  string Name,
  string? LocationId = null);
