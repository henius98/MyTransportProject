namespace MyTransportAppWASM.Services
{
    public class AppStateService
    {
        public string? ReturnUrl { get; set; }

        public event Action? OnShowLoginRequested;
        public event Action? OnHideLoginRequested;

        public void RequestShowLogin()
        {
            OnShowLoginRequested?.Invoke();
        }

        public void RequestHideLogin()
        {
            OnHideLoginRequested?.Invoke();
        }

        public void ClearReturnUrl()
        {
            ReturnUrl = null;
        }
    }
}
