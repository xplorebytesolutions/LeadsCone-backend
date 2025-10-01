using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using xbytechat.api;
using xbytechat.api.Shared;
using xbytechat.api.WhatsAppSettings.DTOs;
using DTO = xbytechat.api.WhatsAppSettings.DTOs;
namespace xbytechat_api.WhatsAppSettings.Services
{

    public class WhatsAppTemplateFetcherService : IWhatsAppTemplateFetcherService
    {
        private readonly AppDbContext _dbContext;
        private readonly HttpClient _httpClient;
        private readonly ILogger<WhatsAppTemplateFetcherService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        public WhatsAppTemplateFetcherService(AppDbContext dbContext, HttpClient httpClient, ILogger<WhatsAppTemplateFetcherService> logger, IHttpContextAccessor httpContextAccessor)
        {
            _dbContext = dbContext;
            _httpClient = httpClient;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }
        public async Task<List<TemplateMetadataDto>> FetchTemplatesAsync(Guid businessId)
        {
            var templates = new List<TemplateMetadataDto>();

            var setting = await _dbContext.WhatsAppSettings
                .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive);

            if (setting == null)
            {
                _logger.LogWarning("WhatsApp Settings not found for BusinessId: {BusinessId}", businessId);
                return templates;
            }

            var provider = (setting.Provider ?? "").Trim().ToLowerInvariant();
            var baseUrl = setting.ApiUrl?.TrimEnd('/') ?? "";

            try
            {
                if (provider == "meta_cloud")
                {
                    if (string.IsNullOrWhiteSpace(setting.ApiKey) || string.IsNullOrWhiteSpace(setting.WabaId))
                    {
                        _logger.LogWarning("Missing API Token or WABA ID for BusinessId: {BusinessId}", businessId);
                        return templates;
                    }

                    var nextUrl = $"{(string.IsNullOrWhiteSpace(baseUrl) ? "https://graph.facebook.com/v22.0" : baseUrl)}/{setting.WabaId}/message_templates?limit=100";
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setting.ApiKey);

                    while (!string.IsNullOrWhiteSpace(nextUrl))
                    {
                        var response = await _httpClient.GetAsync(nextUrl);
                        var json = await response.Content.ReadAsStringAsync();
                        _logger.LogInformation("📦 Meta Template API Raw JSON for {BusinessId}:\n{Json}", setting.BusinessId, json);

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogError("❌ Failed to fetch templates from Meta: {Response}", json);
                            break;
                        }

                        dynamic parsed = JsonConvert.DeserializeObject(json);
                        templates.AddRange(ParseTemplatesFromMetaLikePayload(parsed));
                        nextUrl = parsed?.paging?.next?.ToString();
                    }

                    return templates;
                }
                else if (provider == "pinnacle")
                {
                    if (string.IsNullOrWhiteSpace(setting.ApiKey))
                    {
                        _logger.LogWarning("Pinnacle API key missing for BusinessId: {BusinessId}", businessId);
                        return templates;
                    }

                    // Pinnacle typically accepts either WABA ID or PhoneNumberId. Prefer WABA.
                    var pathId = !string.IsNullOrWhiteSpace(setting.WabaId)
                        ? setting.WabaId!.Trim()
                        : setting.PhoneNumberId?.Trim();

                    if (string.IsNullOrWhiteSpace(pathId))
                    {
                        _logger.LogWarning("Pinnacle path id missing (WabaId/PhoneNumberId) for BusinessId: {BusinessId}", businessId);
                        return templates;
                    }

                    var nextUrl = $"{(string.IsNullOrWhiteSpace(baseUrl) ? "https://partnersv1.pinbot.ai/v3" : baseUrl)}/{pathId}/message_templates?limit=100";

                    // IMPORTANT: Pinnacle needs apikey header
                    _httpClient.DefaultRequestHeaders.Remove("apikey");
                    _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("apikey", setting.ApiKey);

                    while (!string.IsNullOrWhiteSpace(nextUrl))
                    {
                        var response = await _httpClient.GetAsync(nextUrl);
                        var json = await response.Content.ReadAsStringAsync();
                        _logger.LogInformation("📦 Pinnacle Template API Raw JSON for {BusinessId}:\n{Json}", setting.BusinessId, json);

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogError("❌ Failed to fetch templates from Pinnacle: {Response}", json);
                            break;
                        }

                        // Try to support both "data": [...] and "templates": [...] styles
                        dynamic parsed = JsonConvert.DeserializeObject(json);
                        templates.AddRange(ParseTemplatesFromMetaLikePayload(parsed)); // many BSPs mirror Meta's shape
                        nextUrl = parsed?.paging?.next?.ToString(); // if their API paginates similarly
                                                                    // If no paging in Pinnacle, set nextUrl = null to exit loop
                        if (nextUrl == null) break;
                    }

                    return templates;
                }
                else
                {
                    _logger.LogInformation("Provider {Provider} does not support listing via API in this build.", provider);
                    return templates;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while fetching WhatsApp templates for provider {Provider}.", provider);
                return templates;
            }
        }

        private static IEnumerable<TemplateMetadataDto> ParseTemplatesFromMetaLikePayload(dynamic parsed)
        {
            var list = new List<TemplateMetadataDto>();
            if (parsed == null) return list;

            // Prefer parsed.data; fall back to parsed.templates
            var collection = parsed.data ?? parsed.templates;
            if (collection == null) return list;

            foreach (var tpl in collection)
            {
                string name = tpl.name?.ToString() ?? "";
                string language = tpl.language?.ToString() ?? "en_US";
                string body = "";
                bool hasImageHeader = false;
                var buttons = new List<ButtonMetadataDto>();

                // components may be null for some BSPs
                var components = tpl.components;
                if (components != null)
                {
                    foreach (var component in components)
                    {
                        string type = component.type?.ToString()?.ToUpperInvariant();

                        if (type == "BODY")
                            body = component.text?.ToString() ?? "";

                        if (type == "HEADER" && (component.format?.ToString()?.ToUpperInvariant() == "IMAGE"))
                            hasImageHeader = true;

                        if (type == "BUTTONS" && component.buttons != null)
                        {
                            foreach (var button in component.buttons)
                            {
                                try
                                {
                                    string btnType = button.type?.ToString()?.ToUpperInvariant() ?? "";
                                    string text = button.text?.ToString() ?? "";
                                    int index = buttons.Count;

                                    string subType = btnType switch
                                    {
                                        "URL" => "url",
                                        "PHONE_NUMBER" => "voice_call",
                                        "QUICK_REPLY" => "quick_reply",
                                        "COPY_CODE" => "copy_code",
                                        "CATALOG" => "catalog",
                                        "FLOW" => "flow",
                                        "REMINDER" => "reminder",
                                        "ORDER_DETAILS" => "order_details",
                                        _ => "unknown"
                                    };

                                    string? paramValue =
                                        button.url != null ? button.url.ToString() :
                                        button.phone_number != null ? button.phone_number.ToString() :
                                        button.coupon_code != null ? button.coupon_code.ToString() :
                                        button.flow_id != null ? button.flow_id.ToString() :
                                        null;

                                    // If BSP marks dynamic examples like Meta, respect them; otherwise be lenient
                                    buttons.Add(new ButtonMetadataDto
                                    {
                                        Text = text,
                                        Type = btnType,
                                        SubType = subType,
                                        Index = index,
                                        ParameterValue = paramValue ?? ""
                                    });
                                }
                                catch { /* ignore per-button parsing issues */ }
                            }
                        }
                    }
                }

                int placeholderCount = Regex.Matches(body ?? "", "{{(.*?)}}").Count;

                list.Add(new TemplateMetadataDto
                {
                    Name = name,
                    Language = language,
                    Body = body,
                    PlaceholderCount = placeholderCount,
                    HasImageHeader = hasImageHeader,
                    ButtonParams = buttons
                });
            }

            return list;
        }

        public async Task<List<TemplateForUIResponseDto>> FetchAllTemplatesAsync()
        {
            var result = new List<TemplateForUIResponseDto>();

            var user = _httpContextAccessor.HttpContext.User;
            var businessId = user.GetBusinessId();
            _logger.LogInformation("🔎 Fetching templates for BusinessId {BusinessId}", businessId);

            // 1) Load this business's active setting (provider can be Meta or Pinnacle)
            var setting = await _dbContext.WhatsAppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s =>
                    s.IsActive &&
                    s.BusinessId == businessId);

            if (setting == null)
            {
                _logger.LogWarning("⚠️ No active WhatsApp setting for BusinessId {BusinessId}", businessId);
                return result;
            }

            try
            {
                string provider = setting.Provider?.ToLowerInvariant() ?? "";

                if (provider == "meta_cloud")
                {
                    // ✅ Meta Cloud path → ApiKey + WabaId required
                    if (string.IsNullOrWhiteSpace(setting.ApiKey) || string.IsNullOrWhiteSpace(setting.WabaId))
                    {
                        _logger.LogWarning("⚠️ Missing ApiKey or WabaId for Meta Cloud (Biz {BusinessId})", businessId);
                        return result;
                    }

                    var baseUrl = (setting.ApiUrl?.TrimEnd('/') ?? "https://graph.facebook.com/v22.0");
                    var nextUrl = $"{baseUrl}/{setting.WabaId}/message_templates?limit=100";

                    while (!string.IsNullOrWhiteSpace(nextUrl))
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setting.ApiKey);

                        using var res = await _httpClient.SendAsync(req);
                        var json = await res.Content.ReadAsStringAsync();

                        _logger.LogInformation("📦 Meta Template API (Biz {BusinessId}) payload:\n{Json}", businessId, json);

                        if (!res.IsSuccessStatusCode)
                        {
                            _logger.LogError("❌ Meta template fetch failed (Biz {BusinessId}): {Json}", businessId, json);
                            break;
                        }

                        result.AddRange(ParseMetaTemplates(json));
                        nextUrl = JsonConvert.DeserializeObject<dynamic>(json)?.paging?.next?.ToString();
                    }
                }
                else if (provider == "pinnacle")
                {
                    // ✅ Pinnacle path → ApiKey + PhoneNumberId required
                    if (string.IsNullOrWhiteSpace(setting.ApiKey) || string.IsNullOrWhiteSpace(setting.PhoneNumberId))
                    {
                        _logger.LogWarning("⚠️ Missing ApiKey or PhoneNumberId for Pinnacle (Biz {BusinessId})", businessId);
                        return result;
                    }

                    var baseUrl = (setting.ApiUrl?.TrimEnd('/') ?? "https://partnersv1.pinbot.ai/v3");
                    var nextUrl = $"{baseUrl}/{setting.WabaId}/message_templates";

                    using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                    req.Headers.Add("apikey", setting.ApiKey);

                    using var res = await _httpClient.SendAsync(req);
                    var json = await res.Content.ReadAsStringAsync();

                    _logger.LogInformation("📦 Pinnacle Template API (Biz {BusinessId}) payload:\n{Json}", businessId, json);

                    if (!res.IsSuccessStatusCode)
                    {
                        _logger.LogError("❌ Pinnacle template fetch failed (Biz {BusinessId}): {Json}", businessId, json);
                        return result;
                    }

                    result.AddRange(ParsePinnacleTemplates(json));
                }
                else
                {
                    _logger.LogWarning("⚠️ Unknown provider '{Provider}' for Biz {BusinessId}", provider, businessId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception while fetching templates for BusinessId {BusinessId}", businessId);
            }

            return result;
        }
        private List<TemplateForUIResponseDto> ParseMetaTemplates(string json)
        {
            var list = new List<TemplateForUIResponseDto>();
            dynamic parsed = JsonConvert.DeserializeObject<dynamic>(json);

            foreach (var tpl in parsed.data)
            {
                string status = (tpl.status?.ToString() ?? "").ToUpperInvariant();
                if (status != "APPROVED" && status != "ACTIVE") continue;

                list.Add(BuildTemplateDtoFromComponents(tpl));
            }

            return list;
        }

        private List<TemplateForUIResponseDto> ParsePinnacleTemplates(string json)
        {
            var list = new List<TemplateForUIResponseDto>();
            dynamic parsed = JsonConvert.DeserializeObject<dynamic>(json);

            if (parsed?.data == null) return list;

            foreach (var tpl in parsed.data)
            {
                // Pinnacle may not use status like Meta, adjust filter if needed
                list.Add(BuildTemplateDtoFromComponents(tpl));
            }

            return list;
        }

        private TemplateForUIResponseDto BuildTemplateDtoFromComponents(dynamic tpl)
        {
            string name = tpl.name;
            string language = tpl.language ?? "en_US";
            string body = "";
            bool hasImageHeader = false;
            var buttons = new List<ButtonMetadataDto>();

            foreach (var component in tpl.components)
            {
                string type = component.type?.ToString()?.ToUpperInvariant();

                if (type == "BODY")
                    body = component.text?.ToString() ?? "";

                if (type == "HEADER" && (component.format?.ToString()?.ToUpperInvariant() == "IMAGE"))
                    hasImageHeader = true;

                if (type == "BUTTONS")
                {
                    foreach (var button in component.buttons)
                    {
                        string btnType = button.type?.ToString()?.ToUpperInvariant() ?? "";
                        string text = button.text?.ToString() ?? "";
                        int index = buttons.Count;

                        string subType = btnType switch
                        {
                            "URL" => "url",
                            "PHONE_NUMBER" => "voice_call",
                            "QUICK_REPLY" => "quick_reply",
                            "COPY_CODE" => "copy_code",
                            "CATALOG" => "catalog",
                            "FLOW" => "flow",
                            "REMINDER" => "reminder",
                            "ORDER_DETAILS" => "order_details",
                            _ => "unknown"
                        };

                        string? paramValue = button.url?.ToString() ?? button.phone_number?.ToString();

                        if (subType == "unknown") continue;

                        buttons.Add(new ButtonMetadataDto
                        {
                            Text = text,
                            Type = btnType,
                            SubType = subType,
                            Index = index,
                            ParameterValue = paramValue ?? ""
                        });
                    }
                }
            }

            int placeholderCount = Regex.Matches(body ?? "", "{{(.*?)}}").Count;

            return new TemplateForUIResponseDto
            {
                Name = name,
                Language = language,
                Body = body,
                ParametersCount = placeholderCount,
                HasImageHeader = hasImageHeader,
                ButtonParams = buttons
            };
        }

        public async Task<TemplateMetadataDto?> GetTemplateByNameAsync(Guid businessId, string templateName, bool includeButtons)
        {
            var setting = await _dbContext.WhatsAppSettings
                .FirstOrDefaultAsync(x => x.IsActive && x.BusinessId == businessId);

            if (setting == null)
            {
                _logger.LogWarning("❌ WhatsApp settings not found for business: {BusinessId}", businessId);
                return null;
            }

            var provider = (setting.Provider ?? "meta_cloud").Trim().ToLowerInvariant();
            var wabaId = setting.WabaId?.Trim();
            if (string.IsNullOrWhiteSpace(wabaId))
            {
                _logger.LogWarning("❌ Missing WABA ID for business: {BusinessId}", businessId);
                return null;
            }

            // Build URL + request with per-request headers
            string url;
            using var req = new HttpRequestMessage(HttpMethod.Get, "");

            if (provider == "pinnacle")
            {
                // Pinnacle: require ApiKey; use WabaId for template listing
                if (string.IsNullOrWhiteSpace(setting.ApiKey))
                {
                    _logger.LogWarning("❌ ApiKey missing for Pinnacle provider (BusinessId {BusinessId})", businessId);
                    return null;
                }

                var baseUrl = string.IsNullOrWhiteSpace(setting.ApiUrl)
                    ? "https://partnersv1.pinbot.ai/v3"
                    : setting.ApiUrl.TrimEnd('/');

                url = $"{baseUrl}/{wabaId}/message_templates?limit=200";
                // add header variants
                req.Headers.TryAddWithoutValidation("apikey", setting.ApiKey);
                req.Headers.TryAddWithoutValidation("x-api-key", setting.ApiKey);
                // safety: also append as query (some edges require it)
                url = url.Contains("apikey=") ? url : $"{url}&apikey={Uri.EscapeDataString(setting.ApiKey)}";
            }
            else // meta_cloud
            {
                // Meta Cloud: require ApiKey; use WabaId for template listing
                if (string.IsNullOrWhiteSpace(setting.ApiKey))
                {
                    _logger.LogWarning("❌ ApiKey missing for Meta provider (BusinessId {BusinessId})", businessId);
                    return null;
                }

                var baseUrl = string.IsNullOrWhiteSpace(setting.ApiUrl)
                    ? "https://graph.facebook.com/v22.0"
                    : setting.ApiUrl.TrimEnd('/');

                url = $"{baseUrl}/{wabaId}/message_templates?limit=200";
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", setting.ApiKey);
            }

            req.RequestUri = new Uri(url);
            var response = await _httpClient.SendAsync(req);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("❌ Failed to fetch templates (provider={Provider}) for BusinessId {BusinessId}: HTTP {Status} Body: {Body}",
                    provider, businessId, (int)response.StatusCode, json);
                return null;
            }

            try
            {
                dynamic parsed = JsonConvert.DeserializeObject<dynamic>(json);
                var data = parsed?.data;
                if (data == null)
                {
                    _logger.LogWarning("⚠️ No 'data' array in template response (provider={Provider})", provider);
                    return null;
                }

                foreach (var tpl in data)
                {
                    string name = tpl.name;
                    if (!name.Equals(templateName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string language = tpl.language != null ? (string)tpl.language : "en_US";
                    string body = "";
                    var buttons = new List<ButtonMetadataDto>();
                    bool hasImageHeader = false;

                    // components loop
                    foreach (var component in tpl.components)
                    {
                        string type = component.type?.ToString()?.ToUpperInvariant();

                        if (type == "BODY")
                        {
                            try { body = component.text?.ToString() ?? ""; }
                            catch { body = ""; }
                        }

                        if (type == "HEADER")
                        {
                            string format = component.format?.ToString()?.ToUpperInvariant();
                            if (format == "IMAGE") hasImageHeader = true;
                        }

                        if (includeButtons && type == "BUTTONS")
                        {
                            foreach (var button in component.buttons)
                            {
                                try
                                {
                                    string btnType = button.type?.ToString()?.ToUpperInvariant() ?? "";
                                    string text = button.text?.ToString() ?? "";
                                    int index = buttons.Count;

                                    // normalize sub-type for our app
                                    string subType = btnType switch
                                    {
                                        "URL" => "url",
                                        "PHONE_NUMBER" => "voice_call",
                                        "QUICK_REPLY" => "quick_reply",
                                        "COPY_CODE" => "copy_code",
                                        "CATALOG" => "catalog",
                                        "FLOW" => "flow",
                                        "REMINDER" => "reminder",
                                        "ORDER_DETAILS" => "order_details",
                                        _ => "unknown"
                                    };

                                    // Known dynamic param extraction
                                    string? paramValue = null;
                                    if (button.url != null)
                                        paramValue = button.url.ToString();
                                    else if (button.phone_number != null)
                                        paramValue = button.phone_number.ToString();
                                    else if (button.coupon_code != null)
                                        paramValue = button.coupon_code.ToString();
                                    else if (button.flow_id != null)
                                        paramValue = button.flow_id.ToString();

                                    // Skip truly invalid
                                    if (subType == "unknown" ||
                                        (paramValue == null && new[] { "url", "flow", "copy_code" }.Contains(subType)))
                                    {
                                        _logger.LogWarning("⚠️ Skipping button '{Text}' due to unknown type or missing required param.", text);
                                        continue;
                                    }

                                    buttons.Add(new ButtonMetadataDto
                                    {
                                        Text = text,
                                        Type = btnType,
                                        SubType = subType,
                                        Index = index,
                                        ParameterValue = paramValue ?? "" // empty for static buttons
                                    });
                                }
                                catch (Exception exBtn)
                                {
                                    _logger.LogWarning(exBtn, "⚠️ Failed to parse button in template {TemplateName}", name);
                                }
                            }
                        }
                    }

                    // Count {{n}} placeholders in body
                    int paramCount = Regex.Matches(body ?? "", "{{\\s*\\d+\\s*}}").Count;

                    return new TemplateMetadataDto
                    {
                        Name = name,
                        Language = language,
                        Body = body,
                        PlaceholderCount = paramCount,
                        HasImageHeader = hasImageHeader,
                        ButtonParams = includeButtons ? buttons : new List<ButtonMetadataDto>()
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception while parsing template response");
            }

            return null;
        }
        private static TemplateMetadataDto? ExtractTemplateFromListJson(string json, string templateName, bool includeButtons)
        {
            var root = JObject.Parse(json);
            var data = root["data"] as JArray;
            if (data == null) return null;

            foreach (var tplToken in data.OfType<JObject>())
            {
                var name = tplToken.Value<string>("name") ?? "";
                if (!name.Equals(templateName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var language = tplToken.Value<string>("language") ?? "en_US";
                var components = tplToken["components"] as JArray;

                string body = "";
                bool hasImageHeader = false;
                var buttons = new List<ButtonMetadataDto>();

                if (components != null)
                {
                    foreach (var comp in components.OfType<JObject>())
                    {
                        var type = (comp.Value<string>("type") ?? "").ToUpperInvariant();

                        if (type == "BODY")
                        {
                            body = comp.Value<string>("text") ?? body;
                        }
                        else if (type == "HEADER")
                        {
                            var fmt = (comp.Value<string>("format") ?? "").ToUpperInvariant();
                            if (fmt == "IMAGE") hasImageHeader = true;
                        }
                        else if (includeButtons && type == "BUTTONS")
                        {
                            var btns = comp["buttons"] as JArray;
                            if (btns == null) continue;

                            var idx = 0;
                            foreach (var b in btns.OfType<JObject>())
                            {
                                var btnTypeRaw = (b.Value<string>("type") ?? "").ToUpperInvariant();
                                var text = b.Value<string>("text") ?? "";

                                var subType = btnTypeRaw switch
                                {
                                    "URL" => "url",
                                    "PHONE_NUMBER" => "voice_call",
                                    "QUICK_REPLY" => "quick_reply",
                                    "COPY_CODE" => "copy_code",
                                    "CATALOG" => "catalog",
                                    "FLOW" => "flow",
                                    "REMINDER" => "reminder",
                                    "ORDER_DETAILS" => "order_details",
                                    _ => "unknown"
                                };

                                string? paramValue =
                                    b.Value<string>("url") ??
                                    b.Value<string>("phone_number") ??
                                    b.Value<string>("coupon_code") ??
                                    b.Value<string>("flow_id");

                                // Skip unknown or missing required dynamic values
                                if (subType == "unknown") continue;
                                if ((subType is "url" or "flow" or "copy_code") && string.IsNullOrWhiteSpace(paramValue))
                                    continue;

                                buttons.Add(new ButtonMetadataDto
                                {
                                    Text = text,
                                    Type = btnTypeRaw,
                                    SubType = subType,
                                    Index = idx++,
                                    ParameterValue = paramValue ?? ""
                                });
                            }
                        }
                    }
                }

                var paramCount = Regex.Matches(body ?? "", "{{(.*?)}}").Count;

                return new TemplateMetadataDto
                {
                    Name = name,
                    Language = language,
                    Body = body ?? "",
                    PlaceholderCount = paramCount,
                    HasImageHeader = hasImageHeader,
                    ButtonParams = includeButtons ? buttons : new List<ButtonMetadataDto>()
                };
            }

            return null;
        }

        // --- NEW: DB-backed meta projection methods ---
        // List all template meta for a business (optionally filter by provider)
        public async Task<IReadOnlyList<TemplateMetaDto>> GetTemplatesMetaAsync(Guid businessId, string? provider = null)
        {
            // Adjust DbSet name if different in your AppDbContext
            var q = _dbContext.WhatsAppTemplates
                              .AsNoTracking()
                              .Where(t => t.BusinessId == businessId && t.IsActive);

            if (!string.IsNullOrWhiteSpace(provider))
                q = q.Where(t => t.Provider == provider);

            var rows = await q.ToListAsync();
            return rows.Select(MapRowToMeta).ToList();
        }

        // Get a single template meta by name (+ optional language/provider)
        public async Task<TemplateMetaDto?> GetTemplateMetaAsync(Guid businessId, string templateName, string? language = null, string? provider = null)
        {
            var q = _dbContext.WhatsAppTemplates
                              .AsNoTracking()
                              .Where(t => t.BusinessId == businessId && t.IsActive && t.Name == templateName);

            if (!string.IsNullOrWhiteSpace(language))
                q = q.Where(t => t.Language == language);

            if (!string.IsNullOrWhiteSpace(provider))
                q = q.Where(t => t.Provider == provider);

            // prefer exact language match if provided
            var row = await q.OrderByDescending(t => t.Language == language).FirstOrDefaultAsync();
            return row == null ? null : MapRowToMeta(row);
        }
        // Map persisted catalog row → TemplateMetaDto
        //private DTO.TemplateMetaDto MapRowToMeta(dynamic row)
        //{
        //    var meta = new DTO.TemplateMetaDto
        //    {
        //        Provider = row.Provider ?? "",
        //        TemplateId = row.ExternalId ?? row.TemplateId ?? "",
        //        TemplateName = row.Name ?? "",
        //        Language = row.Language ?? "",
        //        HasHeaderMedia = row.HasImageHeader ?? false,
        //        HeaderType = (row.HasImageHeader ?? false) ? "IMAGE" : ""
        //    };

        //    int count = 0;
        //    try { count = Convert.ToInt32(row.PlaceholderCount ?? 0); } catch { count = 0; }

        //    meta.BodyPlaceholders = Enumerable.Range(1, Math.Max(0, count))
        //                                      .Select(i => new DTO.PlaceholderSlot { Index = i })
        //                                      .ToList();

        //    // ButtonsJson → TemplateButtonMeta[]
        //    meta.Buttons = new List<DTO.TemplateButtonMeta>();
        //    try
        //    {
        //        string buttonsJson = row.ButtonsJson ?? "";
        //        if (!string.IsNullOrWhiteSpace(buttonsJson))
        //        {
        //            using var doc = JsonDocument.Parse(buttonsJson);
        //            if (doc.RootElement.ValueKind == JsonValueKind.Array)
        //            {
        //                int i = 0;
        //                foreach (var el in doc.RootElement.EnumerateArray())
        //                {
        //                    var type = el.TryGetProperty("Type", out var p1) ? p1.GetString() ?? ""
        //                              : el.TryGetProperty("type", out var p1b) ? p1b.GetString() ?? "" : "";
        //                    var text = el.TryGetProperty("Text", out var p2) ? p2.GetString() ?? ""
        //                              : el.TryGetProperty("text", out var p2b) ? p2b.GetString() ?? "" : "";
        //                    var value = el.TryGetProperty("ParameterValue", out var p3) ? p3.GetString()
        //                              : el.TryGetProperty("value", out var p3b) ? p3b.GetString() : null;
        //                    var order = el.TryGetProperty("Index", out var p4) && p4.ValueKind == JsonValueKind.Number
        //                              ? p4.GetInt32()
        //                              : i;

        //                    meta.Buttons.Add(new DTO.TemplateButtonMeta
        //                    {
        //                        Type = type,
        //                        Text = text,
        //                        Value = value,
        //                        Order = order
        //                    });
        //                    i++;
        //                }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogWarning(ex, "Failed to parse ButtonsJson for template {TemplateName}", (string)(row.Name ?? "(unknown)"));
        //    }

        //    return meta;
        //}
        // add near the class top (once)
        // helpers (place near the class top)
        private static readonly Regex _phRx = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

        private static int DistinctMaxPlaceholderIndex(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var m = _phRx.Matches(text);
            int max = 0;
            var seen = new HashSet<int>();
            foreach (Match x in m)
            {
                if (int.TryParse(x.Groups[1].Value, out var i) && seen.Add(i) && i > max)
                    max = i;
            }
            return max;
        }

        private static bool TryGetPropCI(JsonElement obj, string name, out JsonElement value)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private DTO.TemplateMetaDto MapRowToMeta(dynamic row)
        {
            var meta = new DTO.TemplateMetaDto
            {
                Provider = row.Provider ?? "",
                TemplateId = row.ExternalId ?? row.TemplateId ?? "",
                TemplateName = row.Name ?? "",
                Language = row.Language ?? "en_US",
                HasHeaderMedia = false,
                HeaderType = "" // will be set from RawJson
            };

            // --- Detect header kind & body placeholders from RawJson (preferred) ---
            try
            {
                string raw = row.RawJson ?? ""; // ✅ your entity has RawJson, not DefinitionJson
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;

                    // components (case-insensitive)
                    if (TryGetPropCI(root, "components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in comps.EnumerateArray())
                        {
                            // type (HEADER/BODY)
                            if (!TryGetPropCI(c, "type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                                continue;

                            var type = typeEl.GetString();
                            if (string.Equals(type, "HEADER", StringComparison.OrdinalIgnoreCase))
                            {
                                // format (IMAGE/VIDEO/DOCUMENT/TEXT)
                                string fmt = "";
                                if (TryGetPropCI(c, "format", out var fmtEl) && fmtEl.ValueKind == JsonValueKind.String)
                                    fmt = (fmtEl.GetString() ?? "").ToUpperInvariant();

                                switch (fmt)
                                {
                                    case "IMAGE": meta.HasHeaderMedia = true; meta.HeaderType = "IMAGE"; break;
                                    case "VIDEO": meta.HasHeaderMedia = true; meta.HeaderType = "VIDEO"; break;
                                    case "DOCUMENT": meta.HasHeaderMedia = true; meta.HeaderType = "DOCUMENT"; break;
                                    case "TEXT": meta.HasHeaderMedia = false; meta.HeaderType = "TEXT"; break;
                                    default:         /* unknown */ break;
                                }
                            }
                            else if (string.Equals(type, "BODY", StringComparison.OrdinalIgnoreCase))
                            {
                                string? bodyText = null;
                                if (TryGetPropCI(c, "text", out var tx) && tx.ValueKind == JsonValueKind.String)
                                    bodyText = tx.GetString();
                                else
                                    bodyText = row.Body; // fallback

                                var count = DistinctMaxPlaceholderIndex(bodyText);
                                meta.BodyPlaceholders = Enumerable.Range(1, Math.Max(0, count))
                                                                  .Select(i => new DTO.PlaceholderSlot { Index = i })
                                                                  .ToList();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RawJson parse failed for template {TemplateName}", (string)(row.Name ?? "(unknown)"));
            }

            // --- Fallbacks when raw json missing/partial ---
            if (string.IsNullOrEmpty(meta.HeaderType) && (row.HasImageHeader ?? false))
            {
                meta.HasHeaderMedia = true;
                meta.HeaderType = "IMAGE"; // legacy flag
            }

            if (meta.BodyPlaceholders == null || meta.BodyPlaceholders.Count == 0)
            {
                int count;
                try { count = Convert.ToInt32(row.PlaceholderCount ?? 0); } catch { count = 0; }
                meta.BodyPlaceholders = Enumerable.Range(1, Math.Max(0, count))
                                                  .Select(i => new DTO.PlaceholderSlot { Index = i })
                                                  .ToList();
            }

            // --- ButtonsJson → TemplateButtonMeta[] (robust parsing) ---
            meta.Buttons = new List<DTO.TemplateButtonMeta>();
            try
            {
                string buttonsJson = row.ButtonsJson ?? "";
                if (!string.IsNullOrWhiteSpace(buttonsJson))
                {
                    using var bdoc = JsonDocument.Parse(buttonsJson);
                    if (bdoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        int i = 0;
                        foreach (var el in bdoc.RootElement.EnumerateArray())
                        {
                            var type = el.TryGetProperty("Type", out var pType) ? pType.GetString() ?? ""
                                     : el.TryGetProperty("type", out var pType2) ? pType2.GetString() ?? "" : "";

                            var text = el.TryGetProperty("Text", out var pText) ? pText.GetString() ?? ""
                                     : el.TryGetProperty("text", out var pText2) ? pText2.GetString() ?? "" : "";

                            // value can be ParameterValue / parameterValue / url / Url / value
                            string? value =
                                el.TryGetProperty("ParameterValue", out var pVal1) ? pVal1.GetString() :
                                el.TryGetProperty("parameterValue", out var pVal1b) ? pVal1b.GetString() :
                                el.TryGetProperty("url", out var pVal2) ? pVal2.GetString() :
                                el.TryGetProperty("Url", out var pVal2b) ? pVal2b.GetString() :
                                el.TryGetProperty("value", out var pVal3) ? pVal3.GetString() :
                                null;

                            // order can be Index/index/Position/position; default to 1-based
                            int order =
                                (el.TryGetProperty("Index", out var pIdx) && pIdx.ValueKind == JsonValueKind.Number) ? pIdx.GetInt32() :
                                (el.TryGetProperty("index", out var pIdx2) && pIdx2.ValueKind == JsonValueKind.Number) ? pIdx2.GetInt32() :
                                (el.TryGetProperty("Position", out var pPos) && pPos.ValueKind == JsonValueKind.Number) ? pPos.GetInt32() :
                                (el.TryGetProperty("position", out var pPos2) && pPos2.ValueKind == JsonValueKind.Number) ? pPos2.GetInt32() :
                                (i + 1);

                            meta.Buttons.Add(new DTO.TemplateButtonMeta
                            {
                                Type = type,
                                Text = text,
                                Value = value,
                                Order = order
                            });
                            i++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse ButtonsJson for template {TemplateName}", (string)(row.Name ?? "(unknown)"));
            }

            return meta;
        }


    }
}

