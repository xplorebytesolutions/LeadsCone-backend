using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using xbytechat.api.WhatsAppSettings.Abstractions;
using xbytechat.api.WhatsAppSettings.DTOs;
using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat.api.WhatsAppSettings.Providers
{
    public sealed class MetaTemplateCatalogProvider : ITemplateCatalogProvider
    {
        private readonly HttpClient _http;
        private readonly ILogger<MetaTemplateCatalogProvider> _log;

        public MetaTemplateCatalogProvider(HttpClient http, ILogger<MetaTemplateCatalogProvider> log)
        { _http = http; _log = log; }

        public async Task<IReadOnlyList<TemplateCatalogItem>> ListAsync(WhatsAppSettingEntity s, CancellationToken ct = default)
        {
            var items = new List<TemplateCatalogItem>();
            if (string.IsNullOrWhiteSpace(s.ApiKey) || string.IsNullOrWhiteSpace(s.WabaId))
                return items;

            var baseUrl = s.ApiUrl?.TrimEnd('/') ?? "https://graph.facebook.com/v22.0";
            var next = $"{baseUrl}/{s.WabaId}/message_templates?limit=100";

            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);

            while (!string.IsNullOrWhiteSpace(next))
            {
                var res = await _http.GetAsync(next, ct);
                var json = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) break;

                dynamic parsed = JsonConvert.DeserializeObject(json);

                foreach (var tpl in parsed.data)
                {
                    // Filter APPROVED/ACTIVE
                    string status = (tpl.status?.ToString() ?? "").ToUpperInvariant();
                    if (status != "APPROVED" && status != "ACTIVE") continue;

                    string name = tpl.name;
                    string language = tpl.language ?? "en_US";
                    string body = "";
                    bool hasImageHeader = false;
                    var buttons = new List<ButtonMetadataDto>();

                    foreach (var comp in tpl.components)
                    {
                        string type = comp.type?.ToString()?.ToUpperInvariant();

                        if (type == "BODY")
                            body = comp.text?.ToString() ?? "";

                        if (type == "HEADER" && (comp.format?.ToString()?.ToUpperInvariant() == "IMAGE"))
                            hasImageHeader = true;

                        if (type == "BUTTONS")
                        {
                            foreach (var b in comp.buttons)
                            {
                                try
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

                                    string? param = b.url != null ? b.url.ToString()
                                                 : b.phone_number != null ? b.phone_number.ToString()
                                                 : b.coupon_code != null ? b.coupon_code.ToString()
                                                 : b.flow_id != null ? b.flow_id.ToString()
                                                 : null;

                                    bool hasExample = b.example != null;
                                    bool isDynamic = hasExample && Regex.IsMatch(b.example.ToString(), @"\{\{[0-9]+\}\}");
                                    bool requiresParam = new[] { "url", "flow", "copy_code", "catalog", "reminder" }.Contains(subType);
                                    bool needsRuntimeValue = requiresParam && isDynamic;
                                    if (subType == "unknown" || (param == null && needsRuntimeValue)) continue;

                                    buttons.Add(new ButtonMetadataDto
                                    {
                                        Text = text,
                                        Type = btnType,
                                        SubType = subType,
                                        Index = index,
                                        ParameterValue = param ?? ""
                                    });
                                }
                                catch (Exception ex)
                                { _log.LogWarning(ex, "Button parse failed for template {Name}", (string)name); }
                            }
                        }
                    }

                    int placeholders = Regex.Matches(body ?? "", "{{(.*?)}}").Count;
                    var raw = JsonConvert.SerializeObject(tpl);

                    items.Add(new TemplateCatalogItem(
                        Name: name,
                        Language: language,
                        Body: body,
                        PlaceholderCount: placeholders,
                        HasImageHeader: hasImageHeader,
                        Buttons: buttons,
                        Status: status,
                        Category: tpl.category?.ToString(),
                        ExternalId: tpl.id?.ToString(),
                        RawJson: raw
                    ));
                }

                next = parsed?.paging?.next?.ToString();
            }

            return items;
        }

        public async Task<TemplateCatalogItem?> GetByNameAsync(WhatsAppSettingEntity s, string templateName, CancellationToken ct = default)
            => (await ListAsync(s, ct)).FirstOrDefault(t => t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));
    }
}