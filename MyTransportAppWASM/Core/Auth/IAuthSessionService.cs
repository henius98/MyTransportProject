namespace MyTransportAppWASM.Core.Auth
{
  public interface IAuthSessionService
  {
    Task SignInWithGoogleAsync(string? clientId = null);
    Task SignOutAsync();
  }
}
