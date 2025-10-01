// 📄 File: Features/MessagesEngine/Providers/PinnacleProvider.cs
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using xbytechat.api.Features.MessagesEngine.Abstractions;
using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat.api.Features.MessagesEngine.Providers
{
    public class PinnacleProvider : IWhatsAppProvider
    {
        private readonly HttpClient _http;
        private readonly ILogger<PinnacleProvider> _logger;
        private readonly WhatsAppSettingEntity _setting;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public PinnacleProvider(HttpClient http, ILogger<PinnacleProvider> logger, WhatsAppSettingEntity setting)
        {
            _http = http;
            _logger = logger;
            _setting = setting;
        }

        private string? ResolvePathIdOrNull()
        {
            if (!string.IsNullOrWhiteSpace(_setting.PhoneNumberId)) return _setting.PhoneNumberId!;
            if (!string.IsNullOrWhiteSpace(_setting.WabaId)) return _setting.WabaId!;
            return null;
        }

        private string BuildBaseUrl()
        {
            var baseUrl = string.IsNullOrWhiteSpace(_setting.ApiUrl)
                ? "https://partnersv1.pinbot.ai"
                : _setting.ApiUrl.TrimEnd('/');

            if (!baseUrl.EndsWith("/v3"))
                baseUrl += "/v3";

            return baseUrl;
        }

        // 🔒 Hard-append apikey ALWAYS (no conditions)
        private string BuildSendUrlWithApiKey(string pathId)
        {
            // NEW LOGIC: Check if the 'pathId' is already a full URL.
            // This is how we detect our tracking links.
            if (Uri.IsWellFormedUriString(pathId, UriKind.Absolute))
            {
                // If it's a full URL, use it directly without modification.
                return pathId;
            }

            // OLD LOGIC: If it's just a path ID, build the full Pinnacle API URL.
            var baseUrl = string.IsNullOrWhiteSpace(_setting.ApiUrl)
                ? "https://partnersv1.pinbot.ai"
                : _setting.ApiUrl.TrimEnd('/');

            if (!baseUrl.EndsWith("/v3"))
                baseUrl += "/v3";

            return $"{baseUrl}/{pathId}/messages?apikey={System.Uri.EscapeDataString(_setting.ApiKey)}";
        }

        private async Task<WaSendResult> PostAsync(object payload)
        {
            var pathId = ResolvePathIdOrNull();
            if (string.IsNullOrWhiteSpace(pathId))
            {
                const string err = "Pinnacle: PhoneNumberId or WabaId is required.";
                _logger.LogError(err);
                return new WaSendResult(false, "Pinnacle", null, null, null, err);
            }

            if (string.IsNullOrWhiteSpace(_setting.ApiKey))
            {
                const string err = "Pinnacle: ApiKey is missing in WhatsApp settings.";
                _logger.LogError(err);
                return new WaSendResult(false, "Pinnacle", null, null, null, err);
            }

            var url = BuildSendUrlWithApiKey(pathId);
            var json = JsonSerializer.Serialize(payload, _jsonOpts);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // ✅ Put key in ALL the places some tenants require
            req.Headers.Remove("apikey");
            req.Headers.Remove("x-api-key");
            req.Headers.TryAddWithoutValidation("apikey", _setting.ApiKey);
            req.Headers.TryAddWithoutValidation("x-api-key", _setting.ApiKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Apikey", _setting.ApiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // 🔍 PROVE the headers exist BEFORE sending
            var headerNames = req.Headers.Select(h => $"{h.Key}:{string.Join(",", h.Value.Select(v => v.Length > 4 ? v[..4] + "..." : v))}").ToArray();
            _logger.LogInformation("Pinnacle POST {Url} | Headers => {Headers}", url, string.Join(" | ", headerNames));
            var querycode=req.Content.ReadAsStringAsync().Result;
            var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Pinnacle send failed (HTTP {Status}): {Body}", (int)res.StatusCode, body);
                return new WaSendResult(false, "Pinnacle", null, res.StatusCode, body, res.ReasonPhrase);
            }

            string? id = TryGetPinnMessageId(body);
            return new WaSendResult(true, "Pinnacle", id, res.StatusCode, body, null);
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
        {
            components ??= Enumerable.Empty<object>();
            var langValue = languageCode; // use exact string from template metadata
            return PostAsync(new
            {
                messaging_product = "whatsapp",
                to,
                type = "template",
                template = new
                {
                    name = templateName,
                    language = langValue,
                    components
                }
            });
        }

        private static string? TryGetPinnMessageId(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("messages", out var msgs) &&
                    msgs.ValueKind == JsonValueKind.Array &&
                    msgs.GetArrayLength() > 0 &&
                    msgs[0].TryGetProperty("id", out var id0)) return id0.GetString();

                if (root.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.Object)
                {
                    if (msg.TryGetProperty("id", out var id1)) return id1.GetString();
                    if (msg.TryGetProperty("messageId", out var id2)) return id2.GetString();
                }
                if (root.TryGetProperty("message_id", out var id3)) return id3.GetString();
                if (root.TryGetProperty("messageId", out var id4)) return id4.GetString();
                if (root.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("messageId", out var id5)) return id5.GetString();
                if (root.TryGetProperty("id", out var idTop)) return idTop.GetString();
            }
            catch { }
            return null;
        }

        public Task<WaSendResult> SendInteractiveAsync(object fullPayload) => PostAsync(fullPayload);
    }
}


