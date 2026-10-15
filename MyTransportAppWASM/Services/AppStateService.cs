namespace MyTransportAppWASM.Services
{
    public class AppStateService
    {
        public string? ReturnUrl { get; set; }

        public bool IsLoginRequested { get; private set; }

        public event Action? OnShowLoginRequested;

        public void RequestShowLogin()
        {
            IsLoginRequested = true;
            OnShowLoginRequested?.Invoke();
        }

        public void ClearLoginRequest()
        {
            IsLoginRequested = false;
        }

        public string? TakeReturnUrl()
        {
            var returnUrl = ReturnUrl;
            ReturnUrl = null;
            return returnUrl;
        }

        public void ClearReturnUrl()
        {
            ReturnUrl = null;
        }
    }
}
