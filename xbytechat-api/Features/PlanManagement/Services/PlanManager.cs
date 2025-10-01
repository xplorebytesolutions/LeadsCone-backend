using Microsoft.EntityFrameworkCore;
using xbytechat.api.Features.PlanManagement.Models;
using xbytechat.api.Helpers;
using xbytechat.api.Models.BusinessModel;
using xbytechat.api.Shared;

namespace xbytechat.api.Features.PlanManagement.Services
{
    public class PlanManager : IPlanManager
    {
        private readonly AppDbContext _db;

        public PlanManager(AppDbContext db)
        {
            _db = db;
        }

        public async Task<ResponseResult> CheckQuotaBeforeSendingAsync(Guid businessId)
        {
            var business = await _db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId);

            if (business == null)
                return ResponseResult.ErrorInfo("Business not found.", "Invalid business ID");
            if (business?.BusinessPlanInfo?.RemainingMessages <= 0)
            {
                var msg = business?.BusinessPlanInfo?.Plan == PlanType.Trial
                    ? "Trial limit reached. Please upgrade your plan."
                    : "Monthly quota exhausted. Please upgrade or wait for reset.";

                return ResponseResult.ErrorInfo(msg, "Quota limit exceeded");
            }

            return ResponseResult.SuccessInfo("Quota check passed.");
        }

        public Dictionary<string, bool> GetPlanFeatureMap(string plan)
        {
            // Example map — replace with real logic if needed
            if (plan == "Basic")
                return new Dictionary<string, bool>
            {
                { "CATALOG", true },
                { "MESSAGE_SEND", false },
                { "CRM_NOTES", false }
            };

            if (plan == "Advanced")
                return new Dictionary<string, bool>
            {
                { "CATALOG", true },
                { "MESSAGE_SEND", true },
                { "CRM_NOTES", true },
                { "CRM_TAGS", true }
            };

            // Fallback plan
            return new Dictionary<string, bool>();
        }

    }
}
