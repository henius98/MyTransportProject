using Microsoft.AspNetCore.Components.Authorization;

namespace MyTransportAppWASM.Services;

public class ThemeService
{
  private readonly IJSRuntime _js;
  private readonly AuthenticationStateProvider _authStateProvider;
  private bool _isDarkMode = true;
  private string? _userKey = null;

  public event Action? OnThemeChanged;

  public ThemeService(IJSRuntime js, AuthenticationStateProvider authStateProvider)
  {
    _js = js;
    _authStateProvider = authStateProvider;
  }

  public bool IsDarkMode
  {
    get => _isDarkMode;
    private set
    {
      if (_isDarkMode != value)
      {
        _isDarkMode = value;
        OnThemeChanged?.Invoke();
      }
    }
  }

  public async Task InitializeAsync()
  {
    var authState = await _authStateProvider.GetAuthenticationStateAsync();
    var user = authState.User;

    // Use the 'sub' claim (unique Google ID) as a key suffix
    _userKey = user.Identity?.IsAuthenticated == true
        ? user.FindFirst("sub")?.Value
        : "guest";

    IsDarkMode = await _js.InvokeAsync<bool>("themeManager.initialize", _userKey);
  }

  public async Task ToggleThemeAsync()
  {
    IsDarkMode = !IsDarkMode;
    var key = _userKey ?? "guest";
    await _js.InvokeVoidAsync("themeManager.setTheme", IsDarkMode, key);
  }
}
