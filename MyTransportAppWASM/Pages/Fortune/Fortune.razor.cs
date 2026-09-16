using Microsoft.AspNetCore.Components;
using MyTransportAppWASM.Models.BaziFlow;

namespace MyTransportAppWASM.Pages.Fortune
{
    public partial class Fortune
    {
        private bool isLoading = true;
        private bool isSaving = false;
        private bool hasApiKey = false;
        private string? errorMessage = null;
        
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
                hasApiKey = await BaziFlowService.HasApiKeyAsync();

                if (hasApiKey)
                {
                    var profileData = await BaziFlowService.GetProfileAsync();
                    if (profileData?.Profile != null)
                    {
                        profile = profileData.Profile;
                        var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                        fortune = await BaziFlowService.GetDateFortuneAsync(dateStr);
                    }
                    else
                    {
                        profile = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not load BaziFlow data: {ex.Message}");
                errorMessage = "Could not load your fortune settings on this device.";
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task HandleSetupSubmit()
        {
            errorMessage = null;
            isSaving = true;

            try
            {
                await BaziFlowService.SaveApiKeyAsync(setupModel.ApiKey);

                var request = new CreateProfileRequest
                {
                    Gender = setupModel.Gender,
                    BirthDate = setupModel.BirthDate,
                    BirthHour = setupModel.BirthHour,
                    BirthMinute = setupModel.BirthMinute,
                    Location = "Malaysia"
                };

                var success = await BaziFlowService.CreateProfileAsync(request);
                if (success)
                {
                    await LoadDataAsync();
                }
                else
                {
                    errorMessage = "Failed to create profile. Your API key may be invalid or the service is down.";
                    hasApiKey = false;
                    await BaziFlowService.SaveApiKeyAsync(string.Empty);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not save BaziFlow setup: {ex.Message}");
                errorMessage = "Could not save your setup on this device. Check browser storage permissions and try again.";
            }
            finally
            {
                isSaving = false;
            }
        }

        public class SetupModel
        {
            public string ApiKey { get; set; } = string.Empty;
            public int Gender { get; set; } = 1;
            public string BirthDate { get; set; } = DateTime.Now.AddYears(-30).ToString("yyyy-MM-dd");
            public int BirthHour { get; set; } = 12;
            public int BirthMinute { get; set; } = 0;
        }
    }
}
