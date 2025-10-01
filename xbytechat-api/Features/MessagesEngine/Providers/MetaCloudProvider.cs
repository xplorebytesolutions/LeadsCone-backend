// 📄 File: Features/MessagesEngine/Providers/MetaCloudProvider.cs
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using xbytechat.api;
using xbytechat.api.Features.MessagesEngine.Abstractions;
using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat.api.Features.MessagesEngine.Providers
{
    public class MetaCloudProvider : IWhatsAppProvider
    {
        private readonly AppDbContext _db; // kept for future auditing/log enrichment
        private readonly HttpClient _http;
        private readonly ILogger<MetaCloudProvider> _logger;
        private readonly WhatsAppSettingEntity _setting;

        // ✅ Ignore nulls to avoid sending "components": null etc.
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public MetaCloudProvider(
            AppDbContext db,
            HttpClient http,
            ILogger<MetaCloudProvider> logger,
            WhatsAppSettingEntity setting)
        {
            _db = db;
            _http = http;
            _logger = logger;
            _setting = setting;
        }

        private string BuildUrl()
        {
            var baseUrl = string.IsNullOrWhiteSpace(_setting.ApiUrl)
                ? "https://graph.facebook.com/v18.0"
                : _setting.ApiUrl.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(_setting.PhoneNumberId))
            {
                _logger.LogError("MetaCloudProvider: PhoneNumberId is missing for BusinessId {BusinessId}", _setting.BusinessId);
                return $"{baseUrl}/-/messages"; // inert path; will fail fast with clear logs
            }

            return $"{baseUrl}/{_setting.PhoneNumberId}/messages";
        }

        private async Task<WaSendResult> PostAsync(object payload)
        {
            var url = BuildUrl();
            var json = JsonSerializer.Serialize(payload, _jsonOpts);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            if (!string.IsNullOrWhiteSpace(_setting.ApiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _setting.ApiKey);
            }
            else
            {
                _logger.LogWarning("MetaCloudProvider: ApiToken is empty for BusinessId {BusinessId}", _setting.BusinessId);
            }

            var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("MetaCloud send failed (HTTP {Status}): {Body}", (int)res.StatusCode, body);
                return new WaSendResult(
                    Success: false,
                    Provider: "MetaCloud",
                    ProviderMessageId: null,
                    StatusCode: res.StatusCode,
                    RawResponse: body,
                    Error: res.ReasonPhrase
                );
            }

            string? id = null;
            try
            {
                var root = JsonNode.Parse(body);
                id = root?["messages"]?[0]?["id"]?.GetValue<string>();
            }
            catch
            {
                // Keep raw; ID stays null
            }

            return new WaSendResult(
                Success: true,
                Provider: "MetaCloud",
                ProviderMessageId: id,
                StatusCode: res.StatusCode,
                RawResponse: body,
                Error: null
            );
        }

        public Task<WaSendResult> SendTextAsync(string to, string body)
            => PostAsync(new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { preview_url = false, body }
            });

        public Task<WaSendResult> SendTemplateAsync(string to, string templateName, string languageCode, IEnumerable<object> components)
            => PostAsync(new
            {
                messaging_product = "whatsapp",
                to,
                type = "template",
                template = new
                {
                    name = templateName,
                    language = new { code = languageCode },      // ✅ Meta needs { code: "en_US" }
                    components = components ?? System.Linq.Enumerable.Empty<object>() // ✅ never null
                }
            });

        public Task<WaSendResult> SendInteractiveAsync(object fullPayload)
            => PostAsync(fullPayload);
    }
}

