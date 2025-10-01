using Microsoft.EntityFrameworkCore;
using Serilog;
using xbytechat.api.Features.FeatureAccess.DTOs;
using xbytechat.api.Features.FeatureAccessModule.DTOs;
using xbytechat.api.Features.FeatureAccessModule.Models;
using xbytechat.api.Features.PlanManagement.Services;

namespace xbytechat.api.Features.FeatureAccessModule.Services
{
    public class FeatureAccessService : IFeatureAccessService
    {
        private readonly AppDbContext _context;
        private readonly IPlanManager _planManager;
        private readonly ILogger<FeatureAccessService> _logger;

        public FeatureAccessService(AppDbContext context, IPlanManager planManager, ILogger<FeatureAccessService> logger)
        {
            _context = context;
            _planManager = planManager;
            _logger = logger;
        }

        public async Task<IEnumerable<FeatureAccessDto>> GetAllAsync()
        {
            return await _context.FeatureAccess
                .Select(f => new FeatureAccessDto
                {
                    Id = f.Id,
                    BusinessId = f.BusinessId,
                    FeatureName = f.FeatureName,
                    IsEnabled = f.IsEnabled,
                    Notes = f.Notes,
                    CreatedAt = f.CreatedAt
                })
                .ToListAsync();
        }

        public async Task<IEnumerable<FeatureAccessDto>> GetByBusinessIdAsync(Guid businessId)
        {
            return await _context.FeatureAccess
                .Where(f => f.BusinessId == businessId)
                .Select(f => new FeatureAccessDto
                {
                    Id = f.Id,
                    BusinessId = f.BusinessId,
                    FeatureName = f.FeatureName,
                    IsEnabled = f.IsEnabled,
                    Notes = f.Notes,
                    CreatedAt = f.CreatedAt
                })
                .ToListAsync();
        }

        public async Task<FeatureAccessDto?> GetAsync(Guid id)
        {
            var entity = await _context.FeatureAccess.FindAsync(id);
            if (entity == null) return null;

            return new FeatureAccessDto
            {
                Id = entity.Id,
                BusinessId = entity.BusinessId,
                FeatureName = entity.FeatureName,
                IsEnabled = entity.IsEnabled,
                Notes = entity.Notes,
                CreatedAt = entity.CreatedAt
            };
        }

        public async Task<FeatureAccessDto> CreateAsync(FeatureAccessDto dto)
        {
            var entity = new  xbytechat.api.Features.FeatureAccessModule.Models.FeatureAccess
            {
                Id = Guid.NewGuid(),
                BusinessId = dto.BusinessId,
                FeatureName = dto.FeatureName,
                IsEnabled = dto.IsEnabled,
                Notes = dto.Notes,
                CreatedAt = DateTime.UtcNow
            };

            _context.FeatureAccess.Add(entity);
            await _context.SaveChangesAsync();

            dto.Id = entity.Id;
            dto.CreatedAt = entity.CreatedAt;
            return dto;
        }

        public async Task<FeatureAccessDto> UpdateAsync(Guid id, FeatureAccessDto dto)
        {
            var entity = await _context.FeatureAccess.FindAsync(id);
            if (entity == null)
                throw new Exception("FeatureAccess not found");

            entity.FeatureName = dto.FeatureName;
            entity.IsEnabled = dto.IsEnabled;
            entity.Notes = dto.Notes;

            await _context.SaveChangesAsync();

            return dto;
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            var entity = await _context.FeatureAccess.FindAsync(id);
            if (entity == null) return false;

            _context.FeatureAccess.Remove(entity);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<List<FeatureToggleViewDto>> GetToggleViewAsync(Guid businessId, string plan)
        {
            var allFeatures = await _context.FeatureAccess.ToListAsync();

            var planAccessMap = _planManager.GetPlanFeatureMap(plan); // e.g., Dictionary<string, bool>

            var userOverrides = await _context.UserFeatureAccess
                .Where(x => x.BusinessId == businessId)
                .ToDictionaryAsync(x => x.FeatureName, x => x.IsEnabled);

            var result = allFeatures.Select(f => new FeatureToggleViewDto
            {
                FeatureCode = f.FeatureName,
                Group = f.Group,
                Description = f.Description,
                IsAvailableInPlan = planAccessMap.ContainsKey(f.FeatureName),
                IsOverridden = userOverrides.TryGetValue(f.FeatureName, out var val) ? val : null
            }).ToList();

            return result;
        }

        public async Task ToggleFeatureAsync(Guid businessId, string featureCode, bool isEnabled)
        {
            try
            {
                var existing = await _context.UserFeatureAccess
                    .FirstOrDefaultAsync(f => f.BusinessId == businessId && f.FeatureName == featureCode);

                if (existing != null)
                {
                    existing.IsEnabled = isEnabled;
                    _context.UserFeatureAccess.Update(existing);
                }
                else
                {
                    var newAccess = new UserFeatureAccess
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        FeatureName = featureCode,
                        IsEnabled = isEnabled,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _context.UserFeatureAccess.AddAsync(newAccess);
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle feature {FeatureCode} for business {BusinessId}", featureCode, businessId);
                throw;
            }
        }

        public async Task<List<FeatureStatusDto>> GetFeaturesForCurrentUserAsync(Guid businessId)
        {
            var business = await _context.Businesses
                .Include(b => b.BusinessPlanInfo)
                .FirstOrDefaultAsync(b => b.Id == businessId);

            if (business == null)
            {
                _logger.LogError("❌ Business not found for feature access: {BusinessId}", businessId);
                return new List<FeatureStatusDto>();
            }

            var planName = business.BusinessPlanInfo?.Plan.ToString() ?? "basic"; // Fallback to basic
            _logger.LogInformation("📦 Resolved plan for Business {BusinessId}: {Plan}", businessId, planName);

            var planFeatureMap = _planManager.GetPlanFeatureMap(planName);

            var overrides = await _context.FeatureAccess
                .Where(f => f.BusinessId == businessId)
                .ToDictionaryAsync(f => f.FeatureName, f => (bool?)f.IsEnabled);
            foreach (var kv in overrides)
                _logger.LogInformation($"🔧 Feature override: {kv.Key} = {kv.Value}");
            var allFeatures = planFeatureMap.Keys
                .Union(overrides.Keys)
                .Distinct();

            return allFeatures.Select(f => new FeatureStatusDto
            {
                FeatureCode = f,
                IsAvailableInPlan = planFeatureMap.ContainsKey(f),
                IsOverridden = overrides.ContainsKey(f) ? overrides[f] : null
            }).ToList();
        }

        public async Task<List<UserFeatureAccessDto>> GetAllUserPermissionsAsync(Guid businessId)
        {
            var users = await _context.Users
             .Where(u => u.BusinessId == businessId)
             .Select(u => new UserFeatureAccessDto
             {
                 Id = u.Id,
                 FullName = u.Name, // ✅ Fixed
                 Email = u.Email,
                 Role = u.Role.Name, // ✅ Assuming you want role name string
                 Permissions = _context.UserFeatureAccess
                     .Where(p => p.BusinessId == businessId && p.UserId == u.Id)
                     .Select(p => new FeaturePermissionDto
                     {
                         FeatureName = p.FeatureName,
                         IsEnabled = p.IsEnabled
                     })
                     .ToList()
             }).ToListAsync();


            return users;
        }
        public async Task<Dictionary<string, bool>> GetFeatureMapByBusinessIdAsync(Guid businessId)
        {
            return await _context.FeatureAccess
                .Where(f => f.BusinessId == businessId)
                .ToDictionaryAsync(f => f.FeatureName.ToLower(), f => f.IsEnabled);
        }
        public async Task<Dictionary<string, bool>> GetAllFeatureCodesAsync()
        {
            return await _context.FeatureMasters
                .Select(f => f.FeatureCode.ToLower())
                .Distinct()
                .ToDictionaryAsync(k => k, v => true);
        }


    }
}


