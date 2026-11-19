namespace MyTransportAppWASM.Core.Auth
{
  internal static class AuthLoginHelpers
  {
    public static bool IsSafeReturnUrl(string? returnUrl, string baseUri, Func<string, Uri> toAbsoluteUri)
    {
      if (string.IsNullOrWhiteSpace(returnUrl)) return false;
      try
      {
        var target = toAbsoluteUri(returnUrl);
        var appBase = new Uri(baseUri, UriKind.Absolute);
        return appBase.IsBaseOf(target);
      }
      catch (UriFormatException)
      {
        return false;
      }
    }

    public static (string? ErrorMessage, bool ShouldLog) MapSignInError(string? message)
    {
      if (string.IsNullOrEmpty(message))
      {
        return ("Failed to sign in. Please try again.", true);
      }

      if (message.Contains("popup-closed-by-user", StringComparison.OrdinalIgnoreCase))
      {
        return (null, false);
      }

      if (message.Contains("popup-blocked", StringComparison.OrdinalIgnoreCase))
      {
        return ("The sign-in pop-up was blocked by your browser. Please allow pop-ups for this site, or try again.", true);
      }

      if (message.Contains("unauthorized-domain", StringComparison.OrdinalIgnoreCase))
      {
        return ("This domain is not authorized for Firebase Authentication. Please use localhost instead of 127.0.0.1, or add this domain in Firebase Console.", true);
      }

      return ("Failed to sign in. Please try again.", true);
    }
  }
}
