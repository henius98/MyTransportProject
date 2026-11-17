namespace MyTransportAppWASM.Features.Google.Models;

public sealed record PlaceSuggestion(
  double Lat,
  double Lng,
  string FormattedAddress,
  string Name,
  string? LocationId = null);
