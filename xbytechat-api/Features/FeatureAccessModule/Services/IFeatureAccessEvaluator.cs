using System;
using System.Threading.Tasks;

namespace xbytechat.api.Features.FeatureAccessModule.Services
{
    public interface IFeatureAccessEvaluator
    {
        Task<bool> CanUseAsync(Guid businessId, string featureName, Guid? userId = null);

    }
}
