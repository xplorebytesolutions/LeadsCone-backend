using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using xbytechat.api.WhatsAppSettings.Abstractions;
using xbytechat.api.WhatsAppSettings.DTOs;
using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat.api.WhatsAppSettings.Providers
{
    public sealed class PinnacleTemplateCatalogProvider : ITemplateCatalogProvider
    {
        private readonly HttpClient _http;
        private readonly ILogger<PinnacleTemplateCatalogProvider> _log;

        public PinnacleTemplateCatalogProvider(HttpClient http, ILogger<PinnacleTemplateCatalogProvider> log)
        { _http = http; _log = log; }

        public async Task<IReadOnlyList<TemplateCatalogItem>> ListAsync(WhatsAppSettingEntity s, CancellationToken ct = default)
        {
            var items = new List<TemplateCatalogItem>();

            if (string.IsNullOrWhiteSpace(s.ApiKey))
            {
                _log.LogWarning("Pinnacle: missing ApiKey for BusinessId {BusinessId}", s.BusinessId);
                return items;
            }

            var baseUrl = (s.ApiUrl ?? "https://partnersv1.pinbot.ai/v3").TrimEnd('/');
            var pathId = !string.IsNullOrWhiteSpace(s.WabaId) ? s.WabaId!.Trim()
                        : !string.IsNullOrWhiteSpace(s.PhoneNumberId) ? s.PhoneNumberId!.Trim()
                        : null;

            if (string.IsNullOrWhiteSpace(pathId))
            {
                _log.LogWarning("Pinnacle: missing WabaId/PhoneNumberId for BusinessId {BusinessId}", s.BusinessId);
                return items;
            }

            // set header
            _http.DefaultRequestHeaders.Remove("apikey");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("apikey", s.ApiKey);

            string? nextUrl = $"{baseUrl}/{pathId}/message_templates?limit=100";

            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                var res = await _http.SendAsync(req, ct);
                var json = await res.Content.ReadAsStringAsync(ct);

                if (!res.IsSuccessStatusCode)
                {
                    _log.LogError("❌ Pinnacle list failed ({Status}): {Body}", (int)res.StatusCode, json);
                    break;
                }

                dynamic parsed = JsonConvert.DeserializeObject(json);
                var collection = parsed?.data ?? parsed?.templates;
                if (collection == null)
                {
                    _log.LogInformation("Pinnacle: no data/templates array.");
                    break;
                }

                foreach (var tpl in collection)
                {
                    try
                    {
                        string name = tpl.name?.ToString() ?? "";
                        string language = tpl.language?.ToString() ?? "en_US";
                        string status = (tpl.status?.ToString() ?? "APPROVED").ToUpperInvariant();
                        string category = tpl.category?.ToString();
                        string externalId = tpl.id?.ToString();

                        string body = "";
                        bool hasImageHeader = false;
                        var buttons = new List<ButtonMetadataDto>();

                        var components = tpl.components;
                        if (components != null)
                        {
                            foreach (var c in components)
                            {
                                string type = c.type?.ToString()?.ToUpperInvariant();

                                if (type == "BODY")
                                    body = c.text?.ToString() ?? "";

                                if (type == "HEADER" &&
                                    (c.format?.ToString()?.ToUpperInvariant() == "IMAGE"))
                                    hasImageHeader = true;

                                if (type == "BUTTONS" && c.buttons != null)
                                {
                                    foreach (var b in c.buttons)
                                    {
                                        string btnType = b.type?.ToString()?.ToUpperInvariant() ?? "";
                                        string text = b.text?.ToString() ?? "";
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

                                        string? param =
                                            b.url != null ? b.url.ToString() :
                                            b.phone_number != null ? b.phone_number.ToString() :
                                            b.coupon_code != null ? b.coupon_code.ToString() :
                                            b.flow_id != null ? b.flow_id.ToString() :
                                            null;

                                        buttons.Add(new ButtonMetadataDto
                                        {
                                            Text = text,
                                            Type = btnType,
                                            SubType = subType,
                                            Index = index,
                                            ParameterValue = param ?? ""
                                        });
                                    }
                                }
                            }
                        }

                        int placeholders = Regex.Matches(body ?? "", "{{(.*?)}}").Count;
                        var raw = JsonConvert.SerializeObject(tpl);

                        // Only persist APPROVED/ACTIVE (match Meta behavior)
                        if (status is "APPROVED" or "ACTIVE")
                        {
                            items.Add(new TemplateCatalogItem(
                                Name: name,
                                Language: language,
                                Body: body,
                                PlaceholderCount: placeholders,
                                HasImageHeader: hasImageHeader,
                                Buttons: buttons,
                                Status: status,
                                Category: category,
                                ExternalId: externalId,
                                RawJson: raw
                            ));
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Pinnacle: failed to map a template item.");
                    }
                }

                // try to follow paging like Meta
                nextUrl = parsed?.paging?.next?.ToString();
                if (string.IsNullOrWhiteSpace(nextUrl))
                    break;
            }

            return items;
        }

        public Task<TemplateCatalogItem?> GetByNameAsync(WhatsAppSettingEntity s, string templateName, CancellationToken ct = default)
            => Task.FromResult<TemplateCatalogItem?>(null); // not needed for sync path
    }
}


//using xbytechat.api.WhatsAppSettings.Abstractions;
//using xbytechat_api.WhatsAppSettings.Models;

//namespace xbytechat.api.WhatsAppSettings.Providers
//{
//    public sealed class PinnacleTemplateCatalogProvider : ITemplateCatalogProvider
//    {
//        private readonly HttpClient _http;
//        private readonly ILogger<PinnacleTemplateCatalogProvider> _log;

//        public PinnacleTemplateCatalogProvider(HttpClient http, ILogger<PinnacleTemplateCatalogProvider> log)
//        { _http = http; _log = log; }

//        public async Task<IReadOnlyList<TemplateCatalogItem>> ListAsync(WhatsAppSettingEntity s, CancellationToken ct = default)
//        {
//            // If your BSP supports listing:
//            // var baseUrl = (s.ApiUrl ?? "").TrimEnd('/');
//            // var url = $"{baseUrl}/templates?limit=100";
//            // using var req = new HttpRequestMessage(HttpMethod.Get, url);
//            // req.Headers.Add("x-api-key", s.ApiKey);
//            // var res = await _http.SendAsync(req, ct);
//            // var json = await res.Content.ReadAsStringAsync(ct);
//            // if (!res.IsSuccessStatusCode) return Array.Empty<TemplateCatalogItem>();
//            // dynamic parsed = JsonConvert.DeserializeObject(json);
//            // map to TemplateCatalogItem and return

//            // If not supported (common): return empty to trigger UI fallback
//            _log.LogInformation("Pinnacle does not expose template listing (returning empty).");
//            return Array.Empty<TemplateCatalogItem>();
//        }

//        public Task<TemplateCatalogItem?> GetByNameAsync(WhatsAppSettingEntity s, string templateName, CancellationToken ct = default)
//            => Task.FromResult<TemplateCatalogItem?>(null);
//    }
//}