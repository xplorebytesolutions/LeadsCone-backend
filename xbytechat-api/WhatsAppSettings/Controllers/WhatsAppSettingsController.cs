// 📄 File: WhatsAppSettings/Controllers/WhatsAppSettingsController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using xbytechat.api.Shared; // for User.GetBusinessId()
using xbytechat.api.WhatsAppSettings.Services;
using xbytechat_api.WhatsAppSettings.DTOs;
using xbytechat_api.WhatsAppSettings.Services;

namespace xbytechat_api.WhatsAppSettings.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class WhatsAppSettingsController : ControllerBase
    {
        private readonly IWhatsAppSettingsService _svc;
        private readonly ILogger<WhatsAppSettingsController> _logger;
        private readonly IWhatsAppSenderService _whatsAppSenderService;
       
        public WhatsAppSettingsController(
            IWhatsAppSettingsService svc,
            ILogger<WhatsAppSettingsController> logger, IWhatsAppSenderService whatsAppSenderService)
        {
            _svc = svc;
            _logger = logger;
            _whatsAppSenderService = whatsAppSenderService;

        }

 
        [HttpPut("update")]
        public async Task<IActionResult> UpdateSetting([FromBody] SaveWhatsAppSettingDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new { message = "Invalid input.", errors = ModelState });

            Guid businessId;
            try { businessId = User.GetBusinessId(); dto.BusinessId = businessId; }
            catch { return Unauthorized(new { message = "BusinessId missing or invalid in token." }); }

            await _svc.SaveOrUpdateSettingAsync(dto);
            return Ok(new { message = "Settings saved/updated." });
        }

        // ----------------------------
        // Get the current user's saved settings
        // ----------------------------
        [HttpGet("me")]
        public async Task<IActionResult> GetMySettings()
        {
            var businessId = User.GetBusinessId();
            var setting = await _svc.GetSettingsByBusinessIdAsync(businessId);
            if (setting == null)
                return NotFound(new { message = "❌ WhatsApp settings not found." });

            return Ok(setting);
        }

        // ----------------------------
        // Test connection using values sent in the body (not necessarily saved)
        // Accepts Provider = "Pinnacle" or "Meta_cloud"
        // ----------------------------
        [HttpPost("test-connection")]
        public async Task<IActionResult> TestConnection([FromBody] SaveWhatsAppSettingDto dto)
        {
            if (dto is null)
                return BadRequest(new { message = "❌ Missing request body." });

            var provider = NormalizeProvider(dto.Provider);
            if (provider is null)
                return BadRequest(new { message = "❌ Provider is required (Pinnacle | Meta_cloud)." });

            dto.Provider = provider; // use canonical

            // Minimal provider-specific validation (service will validate again)
            if (provider == "Meta_cloud")
            {
                if (string.IsNullOrWhiteSpace(dto.ApiUrl) ||
                    string.IsNullOrWhiteSpace(dto.ApiKey) ||
                    string.IsNullOrWhiteSpace(dto.PhoneNumberId))
                {
                    return BadRequest(new { message = "❌ API URL, Token and Phone Number ID are required for Meta Cloud test." });
                }
            }
            else if (provider == "Pinnacle")
            {
                if (string.IsNullOrWhiteSpace(dto.ApiUrl) ||
                    string.IsNullOrWhiteSpace(dto.ApiKey) ||
                    (string.IsNullOrWhiteSpace(dto.WabaId) && string.IsNullOrWhiteSpace(dto.PhoneNumberId)) ||
                    string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber))
                {
                    return BadRequest(new
                    {
                        message = "❌ API URL, API Key, (WABA ID or Phone Number ID), and Business Number are required for Pinnacle test."
                    });
                }
            }

            try
            {
                var message = await _svc.TestConnectionAsync(dto);

                // Convention: service returns a human string; we 200 on success (starts with ✅), 400 otherwise
                if (!string.IsNullOrEmpty(message) && message.StartsWith("✅"))
                    return Ok(new { message });

                return BadRequest(new { message = string.IsNullOrEmpty(message) ? "❌ Test failed." : message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [TestConnection] Failed");
                return StatusCode(500, new { message = "❌ Test connection failed.", details = ex.Message });
            }
        }

        // ----------------------------
        // Test connection against the SAVED settings for this business
        // ----------------------------
        //[HttpPost("test-connection/current")]
        //public async Task<IActionResult> TestConnectionCurrent()
        //{
        //    var businessId = User.GetBusinessId();
        //    var saved = await _svc.GetSettingsByBusinessIdAsync(businessId);
        //    if (saved is null)
        //        return NotFound(new { message = "❌ No saved WhatsApp settings found." });

        //    var dto = new SaveWhatsAppSettingDto
        //    {
        //        BusinessId = businessId,
        //        Provider = NormalizeProvider(saved.Provider) ?? saved.Provider,
        //        ApiUrl = saved.ApiUrl,
        //        ApiKey = saved.ApiKey,
        //       // ApiToken = saved.ApiToken,
        //        PhoneNumberId = saved.PhoneNumberId,
        //        WabaId = saved.WabaId,
        //        WhatsAppBusinessNumber = saved.WhatsAppBusinessNumber,
        //        SenderDisplayName = saved.SenderDisplayName,
        //        WebhookSecret = saved.WebhookSecret,
        //        WebhookVerifyToken = saved.WebhookVerifyToken,
        //        IsActive = saved.IsActive
        //    };

        //    try
        //    {
        //        var message = await _svc.TestConnectionAsync(dto);
        //        if (!string.IsNullOrEmpty(message) && message.StartsWith("✅"))
        //            return Ok(new { message });

        //        return BadRequest(new { message = string.IsNullOrEmpty(message) ? "❌ Test failed." : message });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "❌ [TestConnectionCurrent] Failed");
        //        return StatusCode(500, new { message = "❌ Test connection failed.", details = ex.Message });
        //    }
        //}

        // ----------------------------
        // Delete current user's settings
        // ----------------------------

        [HttpPost("test-connection/current")]
        public async Task<IActionResult> TestConnectionCurrent()
        {
            // ---- local helpers (scoped only to this action) ----
            static string? NormalizeProvider(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var s = raw.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
                if (s is "meta_cloud" or "meta" or "wa_cloud" or "facebook" or "whatsapp_cloud")
                    return "META_CLOUD";
                if (s is "pinnacle" or "pinnacle_official" or "pinbot")
                    return "PINNACLE";
                return null; // unknown → let original pass through
            }

            static string NormalizeApiUrl(string? apiUrl, string? provider)
            {
                if (string.Equals(provider, "META_CLOUD", StringComparison.Ordinal))
                    return string.IsNullOrWhiteSpace(apiUrl) ? "https://graph.facebook.com/v22.0" : apiUrl.Trim();
                return string.IsNullOrWhiteSpace(apiUrl) ? string.Empty : apiUrl.Trim();
            }

            static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            // ---- /helpers ----

            var businessId = User.GetBusinessId();
            var saved = await _svc.GetSettingsByBusinessIdAsync(businessId);
            if (saved is null)
                return NotFound(new { message = "❌ No saved WhatsApp settings found." });

            // defensive normalization (safe even if FE already sends uppercase)
            var provider = NormalizeProvider(saved.Provider) ?? saved.Provider?.Trim();
            var apiUrl = NormalizeApiUrl(saved.ApiUrl, provider);
            var apiKey = T(saved.ApiKey);
            var phoneId = T(saved.PhoneNumberId);
            var wabaId = T(saved.WabaId);

            var dto = new SaveWhatsAppSettingDto
            {
                BusinessId = businessId,
                Provider = provider ?? saved.Provider, // keep original if we couldn't map
                ApiUrl = apiUrl,
                ApiKey = apiKey,
                PhoneNumberId = phoneId,
                WabaId = wabaId,
                WhatsAppBusinessNumber = T(saved.WhatsAppBusinessNumber),
                SenderDisplayName = T(saved.SenderDisplayName),
                WebhookSecret = T(saved.WebhookSecret),
                WebhookVerifyToken = T(saved.WebhookVerifyToken),
                IsActive = saved.IsActive
            };

            // Provider-specific guardrails (clear errors instead of failing deep inside)
            if (dto.Provider == "META_CLOUD")
            {
                if (string.IsNullOrEmpty(dto.ApiKey))
                    return BadRequest(new { message = "❌ Missing Meta access token (ApiKey)." });
                // apiUrl defaulted above if empty
            }
            else if (dto.Provider == "PINNACLE")
            {
                if (string.IsNullOrEmpty(dto.ApiKey))
                    return BadRequest(new { message = "❌ Missing Pinnacle API key." });
                if (string.IsNullOrEmpty(dto.ApiUrl))
                    return BadRequest(new { message = "❌ Missing Pinnacle API URL." });
            }
            else
            {
                return BadRequest(new { message = $"❌ Unsupported provider: {dto.Provider}" });
            }

            try
            {
                var message = await _svc.TestConnectionAsync(dto);

                if (!string.IsNullOrEmpty(message) && message.StartsWith("✅"))
                    return Ok(new { message });

                return BadRequest(new { message = string.IsNullOrEmpty(message) ? "❌ Test failed." : message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [TestConnectionCurrent] Failed");
                return StatusCode(500, new { message = "❌ Test connection failed.", details = ex.Message });
            }
        }

        [HttpDelete("delete")]
        public async Task<IActionResult> DeleteSetting()
        {
            var businessId = User.GetBusinessId();
            var deleted = await _svc.DeleteSettingsAsync(businessId);
            if (!deleted) return NotFound(new { message = "❌ No WhatsApp settings found to delete." });
            return Ok(new { message = "🗑️ WhatsApp settings deleted successfully." });
        }

        // Optional alias for FE routes that call /delete-current
        [HttpDelete("delete-current")]
        public Task<IActionResult> DeleteSettingAlias() => DeleteSetting();

        /// <summary>
        /// Maps any incoming text to the canonical provider values we support.
        /// Returns null if unrecognized.
        /// </summary>
        private static string? NormalizeProvider(string? providerRaw)
        {
            if (string.IsNullOrWhiteSpace(providerRaw)) return null;

            var p = providerRaw.Trim();

            // Accept canonical values exactly and a few common variants
            if (string.Equals(p, "Pinnacle", StringComparison.Ordinal)) return "Pinnacle";
            if (string.Equals(p, "Meta_cloud", StringComparison.Ordinal)) return "Meta_cloud";

            // tolerate some user/legacy variants from older UIs
            var lower = p.ToLowerInvariant();
            if (lower is "pinbot" or "pinnacle (official)" or "pinnacle (pinnacle)" or "pinnacle official")
                return "Pinnacle";
            if (lower is "meta cloud" or "meta" or "meta-cloud")
                return "Meta_cloud";

            return null;
        }

        [HttpGet("callback-url")]
        public async Task<IActionResult> GetMyCallbackUrl([FromServices] IConfiguration cfg)
        {
            var businessId = User.GetBusinessId();
            var baseUrl = cfg["App:PublicBaseUrl"] ?? string.Empty;
            var url = await _svc.GetCallbackUrlAsync(businessId, baseUrl);
            return Ok(new { callbackUrl = url });
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAllForBusinessAsync([FromServices] IWhatsAppSettingsService settingsService)
        {
            // Resolve BusinessId from claim/header/query (same pattern used elsewhere)
            if (!TryResolveBusinessId(HttpContext, User, out var businessId))
                return BadRequest("BusinessId is required (claim/header/query).");

            var items = await settingsService.GetAllForBusinessAsync(businessId);
            return Ok(items);
        }

        // local helper (add once in this controller if you don’t already have it)
        private static bool TryResolveBusinessId(HttpContext ctx, ClaimsPrincipal user, out Guid businessId)
        {
            businessId = Guid.Empty;

            var claim = user?.FindFirst("BusinessId") ?? user?.FindFirst("businessId");
            if (claim != null && Guid.TryParse(claim.Value, out businessId))
                return true;

            if (ctx.Request.Headers.TryGetValue("X-Business-Id", out var h)
                && Guid.TryParse(h.ToString(), out businessId))
                return true;

            if (Guid.TryParse(ctx.Request.Query["businessId"], out businessId))
                return true;

            return false;
        }

        // GET /api/whatsapp/senders/{businessId}
        [HttpGet("senders/{businessId:guid}")]     // <-- align action path
        public async Task<IActionResult> GetSenders(Guid businessId)
        {
            if (businessId == Guid.Empty)
                return BadRequest(new { message = "Invalid businessId." });

            var items = await _whatsAppSenderService.GetBusinessSendersAsync(businessId);
            return Ok(items);
        }
    }

}




