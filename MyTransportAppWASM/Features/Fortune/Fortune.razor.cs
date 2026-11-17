using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace MyTransportAppWASM.Features.Fortune
{
    public partial class Fortune : IDisposable
    {
        [CascadingParameter]
        private LanguageService Language { get; set; } = default!;

        private bool isLoading = true;
        private bool isSaving = false;
        private bool hasApiKey = false;
        private string? errorMessage = null;
        private string? apiKeyError = null;
        private string? birthDateError = null;
        private string? birthHourError = null;
        private string? birthMinuteError = null;
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
                        var dateStr = MalaysiaTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
            if (!ValidateSetupModel())
            {
                return;
            }

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

        private bool ValidateSetupModel()
        {
            apiKeyError = string.IsNullOrWhiteSpace(setupModel.ApiKey) ? "API Key is required" : null;
            birthDateError = string.IsNullOrWhiteSpace(setupModel.BirthDate) ? "Birth Date is required" : null;
            birthHourError = setupModel.BirthHour is < 0 or > 23 ? "Birth Hour must be between 0 and 23" : null;
            birthMinuteError = setupModel.BirthMinute is < 0 or > 59 ? "Birth Minute must be between 0 and 59" : null;
            return apiKeyError is null && birthDateError is null && birthHourError is null && birthMinuteError is null;
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
            public string BirthDate { get; set; } = MalaysiaTime.Now.AddYears(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            public int BirthHour { get; set; } = 12;
            public int BirthMinute { get; set; } = 0;
        }
    }
}
