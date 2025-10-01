// 📄 xbytechat_api/WhatsAppSettings/Services/WhatsAppSettingsService.cs
using Microsoft.EntityFrameworkCore;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using xbytechat.api;
using xbytechat_api.WhatsAppSettings.DTOs;
using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat_api.WhatsAppSettings.Services
{
    public class WhatsAppSettingsService : IWhatsAppSettingsService
    {
        private readonly AppDbContext _dbContext;
        private readonly HttpClient _http;                    // kept for any other callers you may have
        private readonly IHttpClientFactory _httpClientFactory;

        public WhatsAppSettingsService(
            AppDbContext dbContext,
            HttpClient http,
            IHttpClientFactory httpClientFactory)
        {
            _dbContext = dbContext;
            _http = http;
            _httpClientFactory = httpClientFactory;
        }

        //public async Task SaveOrUpdateSettingAsync(SaveWhatsAppSettingDto dto)
        //{
        //    if (dto.BusinessId == Guid.Empty)
        //        throw new ArgumentException("Invalid BusinessId provided.", nameof(dto.BusinessId));

        //    // Normalize provider (store lower-case for consistency)
        //    var provider = (dto.Provider ?? "pinnacle").Trim();
        //    if (string.IsNullOrWhiteSpace(provider))
        //        provider = "pinnacle";
        //    var providerNorm = provider.ToLowerInvariant();

        //    // Look up by BusinessId + Provider (case-insensitive)
        //    var existing = await _dbContext.WhatsAppSettings
        //        .FirstOrDefaultAsync(x => x.BusinessId == dto.BusinessId && x.Provider.ToLower() == providerNorm);

        //    if (existing != null)
        //    {
        //        // keep provider normalized
        //        existing.Provider = providerNorm;

        //        // Only overwrite when incoming value is non-empty (avoid wiping secrets/tokens accidentally)
        //        if (!string.IsNullOrWhiteSpace(dto.ApiUrl)) existing.ApiUrl = dto.ApiUrl.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.ApiKey)) existing.ApiKey = dto.ApiKey.Trim();
        //       // if (!string.IsNullOrWhiteSpace(dto.ApiToken)) existing.ApiToken = dto.ApiToken!.Trim();

        //        if (!string.IsNullOrWhiteSpace(dto.PhoneNumberId)) existing.PhoneNumberId = dto.PhoneNumberId!.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber)) existing.WhatsAppBusinessNumber = dto.WhatsAppBusinessNumber!.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.SenderDisplayName)) existing.SenderDisplayName = dto.SenderDisplayName!.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.WabaId)) existing.WabaId = dto.WabaId!.Trim();

        //        // 🔐 Webhook auth fields (optional)
        //        if (!string.IsNullOrWhiteSpace(dto.WebhookSecret)) existing.WebhookSecret = dto.WebhookSecret!.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.WebhookVerifyToken)) existing.WebhookVerifyToken = dto.WebhookVerifyToken!.Trim();

        //        // 🌐 NEW: provider callback URL (optional)
        //        if (!string.IsNullOrWhiteSpace(dto.WebhookCallbackUrl)) existing.WebhookCallbackUrl = dto.WebhookCallbackUrl!.Trim();

        //        existing.IsActive = dto.IsActive;
        //        existing.UpdatedAt = DateTime.UtcNow;
        //    }
        //    else
        //    {
        //        var newSetting = new WhatsAppSettingEntity
        //        {
        //            Id = Guid.NewGuid(),
        //            BusinessId = dto.BusinessId,
        //            Provider = providerNorm,
        //            ApiUrl = (dto.ApiUrl ?? string.Empty).Trim(),
        //            ApiKey = string.IsNullOrWhiteSpace(dto.ApiKey) ? null : dto.ApiKey!.Trim(),
        //           // ApiToken = string.IsNullOrWhiteSpace(dto.ApiToken) ? null : dto.ApiToken!.Trim(),
        //            PhoneNumberId = string.IsNullOrWhiteSpace(dto.PhoneNumberId) ? null : dto.PhoneNumberId!.Trim(),
        //            WhatsAppBusinessNumber = string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber) ? null : dto.WhatsAppBusinessNumber!.Trim(),
        //            SenderDisplayName = string.IsNullOrWhiteSpace(dto.SenderDisplayName) ? null : dto.SenderDisplayName!.Trim(),
        //            WabaId = string.IsNullOrWhiteSpace(dto.WabaId) ? null : dto.WabaId!.Trim(),

        //            // 🔐 Webhook auth fields (optional)
        //            WebhookSecret = string.IsNullOrWhiteSpace(dto.WebhookSecret) ? null : dto.WebhookSecret!.Trim(),
        //            WebhookVerifyToken = string.IsNullOrWhiteSpace(dto.WebhookVerifyToken) ? null : dto.WebhookVerifyToken!.Trim(),

        //            // 🌐 NEW: provider callback URL (optional)
        //            WebhookCallbackUrl = string.IsNullOrWhiteSpace(dto.WebhookCallbackUrl) ? null : dto.WebhookCallbackUrl!.Trim(),

        //            IsActive = dto.IsActive,
        //            CreatedAt = DateTime.UtcNow
        //        };

        //        await _dbContext.WhatsAppSettings.AddAsync(newSetting);
        //    }

        //    await _dbContext.SaveChangesAsync();
        //}
        public async Task SaveOrUpdateSettingAsync(SaveWhatsAppSettingDto dto)
        {
            if (dto.BusinessId == Guid.Empty) throw new ArgumentException("BusinessId required");

            var existing = await _dbContext.WhatsAppSettings
                .FirstOrDefaultAsync(x => x.BusinessId == dto.BusinessId && x.Provider == dto.Provider);

            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(dto.ApiUrl)) existing.ApiUrl = dto.ApiUrl.Trim();
                if (!string.IsNullOrWhiteSpace(dto.ApiKey)) existing.ApiKey = dto.ApiKey.Trim();
                if (!string.IsNullOrWhiteSpace(dto.PhoneNumberId)) existing.PhoneNumberId = dto.PhoneNumberId.Trim();
                if (!string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber)) existing.WhatsAppBusinessNumber = dto.WhatsAppBusinessNumber.Trim();
                if (!string.IsNullOrWhiteSpace(dto.SenderDisplayName)) existing.SenderDisplayName = dto.SenderDisplayName.Trim();
                if (!string.IsNullOrWhiteSpace(dto.WabaId)) existing.WabaId = dto.WabaId.Trim();
                if (!string.IsNullOrWhiteSpace(dto.WebhookSecret)) existing.WebhookSecret = dto.WebhookSecret.Trim();
                if (!string.IsNullOrWhiteSpace(dto.WebhookVerifyToken)) existing.WebhookVerifyToken = dto.WebhookVerifyToken.Trim();
                if (!string.IsNullOrWhiteSpace(dto.WebhookCallbackUrl)) existing.WebhookCallbackUrl = dto.WebhookCallbackUrl.Trim();
                existing.IsActive = dto.IsActive;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                var s = new WhatsAppSettingEntity
                {
                    Id = Guid.NewGuid(),
                    BusinessId = dto.BusinessId,
                    Provider = dto.Provider,                           // must be 'META_CLOUD' or 'PINNACLE'
                    ApiUrl = (dto.ApiUrl ?? string.Empty).Trim(),      // avoid NOT NULL violations
                    ApiKey = (dto.ApiKey ?? string.Empty).Trim(),
                    PhoneNumberId = string.IsNullOrWhiteSpace(dto.PhoneNumberId) ? null : dto.PhoneNumberId.Trim(),
                    WhatsAppBusinessNumber = string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber) ? null : dto.WhatsAppBusinessNumber.Trim(),
                    SenderDisplayName = string.IsNullOrWhiteSpace(dto.SenderDisplayName) ? null : dto.SenderDisplayName.Trim(),
                    WabaId = string.IsNullOrWhiteSpace(dto.WabaId) ? null : dto.WabaId.Trim(),
                    WebhookSecret = string.IsNullOrWhiteSpace(dto.WebhookSecret) ? null : dto.WebhookSecret.Trim(),
                    WebhookVerifyToken = string.IsNullOrWhiteSpace(dto.WebhookVerifyToken) ? null : dto.WebhookVerifyToken.Trim(),
                    WebhookCallbackUrl = string.IsNullOrWhiteSpace(dto.WebhookCallbackUrl) ? null : dto.WebhookCallbackUrl.Trim(),
                    IsActive = dto.IsActive,
                    CreatedAt = DateTime.UtcNow
                };
                await _dbContext.WhatsAppSettings.AddAsync(s);
            }

            await _dbContext.SaveChangesAsync();
        }

        //public async Task SaveOrUpdateSettingAsync(SaveWhatsAppSettingDto dto)
        //{
        //    if (dto.BusinessId == Guid.Empty)
        //        throw new ArgumentException("Invalid BusinessId provided.", nameof(dto.BusinessId));

        //    // Controller already called NormalizeProvider; do it again here to be defensive.
        //    // NormalizeProvider MUST return canonical values: "Pinnacle" or "Meta_cloud".
        //    var providerCanon = NormalizeProvider(dto.Provider);
        //    var providerLc = providerCanon.ToLowerInvariant();

        //    // Look up case-insensitively so legacy rows still match
        //    var existing = await _dbContext.WhatsAppSettings
        //        .FirstOrDefaultAsync(x => x.BusinessId == dto.BusinessId && x.Provider.ToLower() == providerLc);

        //    if (existing != null)
        //    {
        //        // Enforce canonical provider in DB (prevents future FK mismatches)
        //        if (!string.Equals(existing.Provider, providerCanon, StringComparison.Ordinal))
        //            existing.Provider = providerCanon;

        //        // Only overwrite when incoming value is non-empty (avoid nuking secrets)
        //        if (!string.IsNullOrWhiteSpace(dto.ApiUrl)) existing.ApiUrl = dto.ApiUrl.Trim();

        //        // IMPORTANT: keep ApiKey if input is empty
        //        if (!string.IsNullOrWhiteSpace(dto.ApiKey)) existing.ApiKey = dto.ApiKey.Trim();

        //        if (!string.IsNullOrWhiteSpace(dto.PhoneNumberId)) existing.PhoneNumberId = dto.PhoneNumberId.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber)) existing.WhatsAppBusinessNumber = dto.WhatsAppBusinessNumber.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.SenderDisplayName)) existing.SenderDisplayName = dto.SenderDisplayName.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.WabaId)) existing.WabaId = dto.WabaId.Trim();

        //        if (!string.IsNullOrWhiteSpace(dto.WebhookSecret)) existing.WebhookSecret = dto.WebhookSecret.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.WebhookVerifyToken)) existing.WebhookVerifyToken = dto.WebhookVerifyToken.Trim();
        //        if (!string.IsNullOrWhiteSpace(dto.WebhookCallbackUrl)) existing.WebhookCallbackUrl = dto.WebhookCallbackUrl.Trim();

        //        existing.IsActive = dto.IsActive;
        //        existing.UpdatedAt = DateTime.UtcNow;
        //    }
        //    else
        //    {
        //        // Create canonical, NOT NULL-safe stub/row
        //        var newSetting = new WhatsAppSettingEntity
        //        {
        //            Id = Guid.NewGuid(),
        //            BusinessId = dto.BusinessId,
        //            Provider = providerCanon,                         // canonical only
        //            ApiUrl = (dto.ApiUrl ?? string.Empty).Trim(),     // keep NOT NULL safe
        //            ApiKey = string.IsNullOrWhiteSpace(dto.ApiKey) ? string.Empty : dto.ApiKey.Trim(), // avoid null
        //            PhoneNumberId = string.IsNullOrWhiteSpace(dto.PhoneNumberId) ? null : dto.PhoneNumberId.Trim(),
        //            WhatsAppBusinessNumber = string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber) ? null : dto.WhatsAppBusinessNumber.Trim(),
        //            SenderDisplayName = string.IsNullOrWhiteSpace(dto.SenderDisplayName) ? null : dto.SenderDisplayName.Trim(),
        //            WabaId = string.IsNullOrWhiteSpace(dto.WabaId) ? null : dto.WabaId.Trim(),
        //            WebhookSecret = string.IsNullOrWhiteSpace(dto.WebhookSecret) ? null : dto.WebhookSecret.Trim(),
        //            WebhookVerifyToken = string.IsNullOrWhiteSpace(dto.WebhookVerifyToken) ? null : dto.WebhookVerifyToken.Trim(),
        //            WebhookCallbackUrl = string.IsNullOrWhiteSpace(dto.WebhookCallbackUrl) ? null : dto.WebhookCallbackUrl.Trim(),
        //            IsActive = dto.IsActive,
        //            CreatedAt = DateTime.UtcNow
        //        };

        //        await _dbContext.WhatsAppSettings.AddAsync(newSetting);
        //    }

        //    await _dbContext.SaveChangesAsync();
        //}

        public async Task<WhatsAppSettingEntity?> GetSettingsByBusinessIdAsync(Guid businessId)
        {
            return await _dbContext.WhatsAppSettings
                .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive);
        }

        public async Task<bool> DeleteSettingsAsync(Guid businessId)
        {
            var setting = await _dbContext.WhatsAppSettings
                .FirstOrDefaultAsync(x => x.BusinessId == businessId);

            if (setting == null) return false;

            _dbContext.WhatsAppSettings.Remove(setting);
            await _dbContext.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// Provider-aware test connection. Returns a short message (✅/❌ …).
        /// The controller may convert non-✅ messages to 400, etc.
        /// </summary>
        public async Task<string> TestConnectionAsync(SaveWhatsAppSettingDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Provider))
                throw new ArgumentException("Provider is required.");

            // normalize provider and baseUrl
            var provider = dto.Provider.Trim();
            var lower = provider.ToLowerInvariant();
            var baseUrl = (dto.ApiUrl ?? string.Empty).Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("ApiUrl is required.");

            var http = _httpClientFactory.CreateClient();

            // ----- Meta Cloud -----
            if (lower == "meta_cloud")
            {
                if (string.IsNullOrWhiteSpace(dto.ApiKey))
                    throw new ArgumentException("ApiKey is required for Meta Cloud.");
                if (string.IsNullOrWhiteSpace(dto.PhoneNumberId))
                    throw new ArgumentException("PhoneNumberId is required for Meta Cloud.");

                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", dto.ApiKey);

                var url = $"{baseUrl}/{dto.PhoneNumberId}";
                var res = await http.GetAsync(url);
                var body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                    return $"❌ Meta Cloud test failed ({(int)res.StatusCode}). Body: {body}";

                return "✅ Meta Cloud token & phone number ID are valid.";
            }

            // ----- Pinnacle (formerly Pinbot) -----
            if (lower == "pinnacle")
            {
                if (string.IsNullOrWhiteSpace(dto.ApiKey))
                    return "❌ API Key is required for Pinnacle.";

                // Pinnacle requires either phone number id OR WABA id in the path
                var pathId =
                    !string.IsNullOrWhiteSpace(dto.PhoneNumberId) ? dto.PhoneNumberId!.Trim() :
                    !string.IsNullOrWhiteSpace(dto.WabaId) ? dto.WabaId!.Trim() :
                    null;

                if (string.IsNullOrWhiteSpace(pathId))
                    return "❌ Provide PhoneNumberId or WabaId for Pinnacle.";

                if (string.IsNullOrWhiteSpace(dto.WhatsAppBusinessNumber))
                    return "❌ WhatsApp Business Number is required for Pinnacle test.";

                var url = $"{baseUrl}/{pathId}/messages";
                var payload = new
                {
                    to = dto.WhatsAppBusinessNumber,
                    type = "text",
                    text = new { body = "Test message" },
                    messaging_product = "whatsapp"
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("apikey", dto.ApiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var res = await http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    if ((int)res.StatusCode == 401 || (int)res.StatusCode == 403)
                        return $"❌ Pinnacle rejected the API key for id '{pathId}'. Verify the key and id. Body: {body}";

                    return $"❌ Pinnacle test failed ({(int)res.StatusCode}). Body: {body}";
                }

                return "✅ Pinnacle API key and endpoint are reachable.";
            }

            return $"❌ Unsupported provider: {dto.Provider}";
        }

        public async Task<string?> GetSenderNumberAsync(Guid businessId)
        {
            var setting = await _dbContext.WhatsAppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive);

            return setting?.WhatsAppBusinessNumber;
        }

        public async Task<string> GetCallbackUrlAsync(Guid businessId, string appBaseUrl)
        {
            var s = await _dbContext.WhatsAppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive);

            if (!string.IsNullOrWhiteSpace(s?.WebhookCallbackUrl))
                return s!.WebhookCallbackUrl!;

            return $"{appBaseUrl.TrimEnd('/')}/api/webhookcallback";
        }
        public async Task<IReadOnlyList<WhatsAppSettingEntity>> GetAllForBusinessAsync(Guid businessId)
        {
            // Return all rows (one per provider) for this business
            var items = await _dbContext.WhatsAppSettings
                .Where(s => s.BusinessId == businessId)
                 .Include(s => s.PhoneNumberId)
                .OrderBy(s => s.Provider)
                .ToListAsync();

            return items.AsReadOnly();
        }
        public async Task<WhatsAppSettingEntity?> GetSettingsByBusinessIdAndProviderAsync(Guid businessId, string provider)
        {
            if (string.IsNullOrWhiteSpace(provider)) return null;
            var prov = provider.Trim();

            // case-insensitive provider match
            return await _dbContext.WhatsAppSettings
                .FirstOrDefaultAsync(s => s.BusinessId == businessId && s.Provider.ToLower() == prov.ToLower());
        }
    }
}

