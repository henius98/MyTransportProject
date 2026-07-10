using Microsoft.AspNetCore.Components;
using MyTransportAppWASM.Models.BaziFlow;

namespace MyTransportAppWASM.Pages.Fortune
{
    public partial class Fortune
    {
        private bool isLoading = true;
        private bool isSaving = false;
        private bool hasApiKey = false;
        
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
                    // Profile might not exist even if API key is present
                    profile = null;
                }
            }
            isLoading = false;
        }

        private async Task HandleSetupSubmit()
        {
            isSaving = true;
            
            await BaziFlowService.SaveApiKeyAsync(setupModel.ApiKey);
            
            var request = new CreateProfileRequest
            {
                Gender = setupModel.Gender,
                BirthDate = setupModel.BirthDate,
                BirthHour = setupModel.BirthHour,
                BirthMinute = setupModel.BirthMinute,
                Location = "Malaysia" // Default location for transport app
            };
            
            var success = await BaziFlowService.CreateProfileAsync(request);
            if (success)
            {
                await LoadDataAsync();
            }
            else
            {
                // In a real app we'd show an error toast here
                hasApiKey = false;
            }
            
            isSaving = false;
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
