using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xbytechat.api.Features.FeatureAccess.DTOs;
using xbytechat.api.Features.FeatureAccessModule.DTOs;

namespace xbytechat.api.Features.FeatureAccessModule.Services
{
    public interface IFeatureAccessService
    {
        Task<IEnumerable<FeatureAccessDto>> GetAllAsync();
        Task<IEnumerable<FeatureAccessDto>> GetByBusinessIdAsync(Guid businessId);
        Task<FeatureAccessDto?> GetAsync(Guid id);
        Task<FeatureAccessDto> CreateAsync(FeatureAccessDto dto);
        Task<FeatureAccessDto> UpdateAsync(Guid id, FeatureAccessDto dto);
        Task<bool> DeleteAsync(Guid id);

        Task<List<FeatureToggleViewDto>> GetToggleViewAsync(Guid businessId, string plan);
        Task ToggleFeatureAsync(Guid businessId, string featureCode, bool isEnabled);
        Task<List<FeatureStatusDto>> GetFeaturesForCurrentUserAsync(Guid businessId);
        Task<List<UserFeatureAccessDto>> GetAllUserPermissionsAsync(Guid businessId);
        Task<Dictionary<string, bool>> GetFeatureMapByBusinessIdAsync(Guid businessId);
        Task<Dictionary<string, bool>> GetAllFeatureCodesAsync();
    }
}
