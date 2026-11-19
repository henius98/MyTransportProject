namespace MyTransportAppWASM.Core.Auth
{
  public sealed record AuthSessionState(FirebaseUser? User, bool IsInitialized)
  {
    public static AuthSessionState Pending { get; } = new(null, IsInitialized: false);
    public static AuthSessionState Anonymous { get; } = new(null, IsInitialized: true);

    public bool IsAuthenticated => User is not null && !string.IsNullOrEmpty(User.Uid);
    public string? UserId => IsAuthenticated ? User!.Uid : null;
    public string DisplayName => !string.IsNullOrEmpty(User?.DisplayName) ? User.DisplayName : "User";
    public string Email => User?.Email ?? "";
    public string? PictureUrl => !string.IsNullOrEmpty(User?.PhotoURL) ? User.PhotoURL : null;
  }

  public interface IAuthSessionService
  {
    AuthSessionState CurrentState { get; }
    event Action<AuthSessionState>? StateChanged;
    Task<AuthSessionState> GetStateAsync();
    Task SignInWithGoogleAsync(string? clientId = null);
    Task SignOutAsync();
  }
}
