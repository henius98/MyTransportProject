using Microsoft.AspNetCore.Components;
using MyTransportAppWASM.Models.BaziFlow;

namespace MyTransportAppWASM.Pages.Fortune
{
    public partial class Fortune : IDisposable
    {
        [CascadingParameter]
        private LanguageService Language { get; set; } = default!;

        private bool isLoading = true;
        private bool isSaving = false;
        private bool hasApiKey = false;
        private string? errorMessage = null;
        private bool _disposed;
        private readonly CancellationTokenSource _cts = new();
        
        private ProfileDetail? profile;
        private FortuneData? fortune;
        
        private SetupModel setupModel = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            isLoading = true;
            errorMessage = null;

            try
            {
                hasApiKey = await BaziFlowService.HasApiKeyAsync(_cts.Token);
                if (_disposed) return;

                if (hasApiKey)
                {
                    var profileData = await BaziFlowService.GetProfileAsync(_cts.Token);
                    if (_disposed) return;
                    if (profileData?.Profile != null)
                    {
                        profile = profileData.Profile;
                        var dateStr = MalaysiaTime.Now.ToString("yyyy-MM-dd");
                        fortune = await BaziFlowService.GetDateFortuneAsync(dateStr, _cts.Token);
                        if (_disposed) return;
                    }
                    else
                    {
                        profile = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    Console.Error.WriteLine($"Could not load BaziFlow data: {ex.Message}");
                    errorMessage = "Could not load your fortune settings on this device.";
                }
            }
            finally
            {
                if (!_disposed)
                {
                    isLoading = false;
                }
            }
        }

        private async Task HandleSetupSubmit()
        {
            errorMessage = null;
            isSaving = true;

            try
            {
                await BaziFlowService.SaveApiKeyAsync(setupModel.ApiKey, _cts.Token);

                var request = new CreateProfileRequest
                {
                    Gender = setupModel.Gender,
                    BirthDate = setupModel.BirthDate,
                    BirthHour = setupModel.BirthHour,
                    BirthMinute = setupModel.BirthMinute,
                    Location = "Malaysia"
                };

                var success = await BaziFlowService.CreateProfileAsync(request, _cts.Token);
                if (_disposed) return;
                if (success)
                {
                    await LoadDataAsync();
                }
                else
                {
                    errorMessage = "Failed to create profile. Your API key may be invalid or the service is down.";
                    hasApiKey = false;
                    await BaziFlowService.SaveApiKeyAsync(string.Empty, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    Console.Error.WriteLine($"Could not save BaziFlow setup: {ex.Message}");
                    errorMessage = "Could not save your setup on this device. Check browser storage permissions and try again.";
                }
            }
            finally
            {
                if (!_disposed)
                {
                    isSaving = false;
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }

        public class SetupModel
        {
            public string ApiKey { get; set; } = string.Empty;
            public int Gender { get; set; } = 1;
            public string BirthDate { get; set; } = MalaysiaTime.Now.AddYears(-30).ToString("yyyy-MM-dd");
            public int BirthHour { get; set; } = 12;
            public int BirthMinute { get; set; } = 0;
        }
    }
}
