using System.Security.Claims;

namespace MyTransportAppWASM.Core.Auth
{
  internal static class ClaimsPrincipalExtensions
  {
    public static string? GetUserId(this ClaimsPrincipal? user) =>
      user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public static string? GetPictureUrl(this ClaimsPrincipal? user) =>
      user?.FindFirst("picture")?.Value;
  }
}
