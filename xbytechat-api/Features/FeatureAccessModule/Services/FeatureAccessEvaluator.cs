using Microsoft.EntityFrameworkCore;
using xbytechat.api.Features.PlanManagement.Models;
using xbytechat.api.Models.BusinessModel;
using xbytechat.api.Shared;

namespace xbytechat.api.Features.FeatureAccessModule.Services
{
    public class FeatureAccessEvaluator : IFeatureAccessEvaluator
    {
        private readonly AppDbContext _db;
        private readonly ILogger<FeatureAccessEvaluator> _logger;

        public FeatureAccessEvaluator(AppDbContext db, ILogger<FeatureAccessEvaluator> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<bool> CanUseAsync(Guid businessId, string featureCode, Guid? userId)
        {
            var business = await _db.Businesses
                .Include(b => b.BusinessPlanInfo)
                .FirstOrDefaultAsync(b => b.Id == businessId);

            if (business == null || business.BusinessPlanInfo == null)
                return false;

            var planEnum = business.BusinessPlanInfo.Plan;
            var planName = Enum.GetName(typeof(PlanType), planEnum)?.ToLower();

            var planFeatures = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["crm"] = planName == "smart" || planName == "advanced",
                ["campaigns"] = planName == "advanced",
                ["catalog"] = true,
                ["automation"] = planName == "smart" || planName == "advanced"
            };

            return planFeatures.TryGetValue(featureCode.ToLower(), out var allowed) && allowed;
        }

    }
}
