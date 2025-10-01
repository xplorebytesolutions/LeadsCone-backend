using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using xbytechat.api;
using FeatureAccessEntity = xbytechat.api.Features.FeatureAccessModule.Models.FeatureAccess;

using xbytechat.api.Features.FeatureAccessModule.Models;

namespace xbytechat.api.Features.FeatureAccessModule.Controllers
{
    [ApiController]
    [Route("api/feature-access")]
    [Authorize]
    public class FeatureAccessController : ControllerBase
    {
        private readonly AppDbContext _db;

        public FeatureAccessController(AppDbContext db)
        {
            _db = db;
        }

        // DTOs to match your frontend shape
        public class FeatureMeDto
        {
            public string featureCode { get; set; } = "";
            public bool isAvailableInPlan { get; set; }
            public bool? isOverridden { get; set; } // null = not overridden
        }

        public class BusinessFeatureDto
        {
            public string featureName { get; set; } = "";
            public bool isEnabled { get; set; }
        }

        /// <summary>
        /// Returns features for the current user, merged from plan + per-business overrides
        /// Shape: [{ featureCode, isAvailableInPlan, isOverridden }]
        /// </summary>
        [HttpGet("me")]
        public async Task<IActionResult> GetMyFeatureAccess()
        {
            var user = HttpContext.User;
            if (user?.Identity is not { IsAuthenticated: true })
                return Unauthorized();

            var role = (user.FindFirst("role")?.Value ?? user.FindFirst(ClaimTypes.Role)?.Value ?? "business").ToLower();
            var plan = (user.FindFirst("plan")?.Value ?? "basic").ToLower();

            Guid businessId = Guid.Empty;
            _ = Guid.TryParse(user.FindFirst("businessId")?.Value, out businessId);

            // Superadmin: grant all known features
            if (role == "superadmin")
            {
                var allNames = await _db.FeatureAccess
                    .AsNoTracking()
                    .Select(f => f.FeatureName)
                    .Distinct()
                    .ToListAsync();

                var super = allNames.Select(name => new FeatureMeDto
                {
                    featureCode = name,
                    isAvailableInPlan = true,
                    isOverridden = true
                });

                return Ok(super);
            }

            // Load plan-level features (same entity type as DbSet)
            var planRows = await _db.FeatureAccess
                .AsNoTracking()
                .Where(f => f.Plan.ToLower() == plan)
                .ToListAsync();

            // Load business overrides using SAME type; avoid ternary type-mismatch
            List<FeatureAccessEntity> overrideRows;
            if (businessId == Guid.Empty)
            {
                overrideRows = new();
            }
            else
            {
                overrideRows = await _db.FeatureAccess
                    .AsNoTracking()
                    .Where(f => f.BusinessId == businessId)
                    .ToListAsync();
            }

            // Build base from plan, then apply overrides
            var map = new Dictionary<string, FeatureMeDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in planRows)
            {
                if (!map.ContainsKey(p.FeatureName))
                {
                    map[p.FeatureName] = new FeatureMeDto
                    {
                        featureCode = p.FeatureName,
                        isAvailableInPlan = p.IsEnabled,
                        isOverridden = null
                    };
                }
                else
                {
                    map[p.FeatureName].isAvailableInPlan = p.IsEnabled;
                }
            }

            foreach (var o in overrideRows)
            {
                if (!map.ContainsKey(o.FeatureName))
                {
                    map[o.FeatureName] = new FeatureMeDto
                    {
                        featureCode = o.FeatureName,
                        isAvailableInPlan = false,
                        isOverridden = o.IsEnabled
                    };
                }
                else
                {
                    map[o.FeatureName].isOverridden = o.IsEnabled;
                }
            }

            return Ok(map.Values.ToArray());
        }

        /// <summary>
        /// Returns all features for a business (used by useAllFeatureAccess/useFeatureAccess hooks)
        /// Shape: [{ featureName, isEnabled }]
        /// </summary>
        [HttpGet("business/{businessId:guid}")]
        public async Task<IActionResult> GetBusinessFeatureAccess([FromRoute] Guid businessId)
        {
            // (Optional) enforce tenant isolation:
            // var currentBiz = HttpContext.User.FindFirst("businessId")?.Value;
            // if (!Guid.TryParse(currentBiz, out var bizFromToken) || bizFromToken != businessId)
            //     return Forbid();

            var rows = await _db.FeatureAccess
                .Where(f => f.BusinessId == businessId)
                .Select(f => new BusinessFeatureDto
                {
                    featureName = f.FeatureName,
                    isEnabled = f.IsEnabled
                })
                .ToListAsync();

            return Ok(rows);
        }
    }
}

