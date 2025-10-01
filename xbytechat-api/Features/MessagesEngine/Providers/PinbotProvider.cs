// 📄 File: Features/MessagesEngine/Providers/PinbotProvider.cs
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using xbytechat.api.Features.MessagesEngine.Abstractions;
using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat.api.Features.MessagesEngine.Providers
{
    public class PinbotProvider : IWhatsAppProvider
    {
        private readonly HttpClient _http;
        private readonly ILogger<PinbotProvider> _logger;
        private readonly WhatsAppSettingEntity _setting;

        public PinbotProvider(
            HttpClient http,
            ILogger<PinbotProvider> logger,
            WhatsAppSettingEntity setting)
        {
            _http = http;
            _logger = logger;
            _setting = setting;
        }

        // Pinbot path segment can be WABA ID *or* PhoneNumberId depending on their account setup.
        // Prefer PhoneNumberId, fall back to WabaId.
        private string? ResolvePathIdOrNull()
        {
            if (!string.IsNullOrWhiteSpace(_setting.PhoneNumberId)) return _setting.PhoneNumberId!;
            if (!string.IsNullOrWhiteSpace(_setting.WabaId)) return _setting.WabaId!;
            return null;
        }

        private string BuildUrl(string pathId)
        {
            var baseUrl = string.IsNullOrWhiteSpace(_setting.ApiUrl)
                ? "https://partnersv1.pinbot.ai"
                : _setting.ApiUrl.TrimEnd('/');

            // Pinbot uses /v3/{id}/messages (id can be WABA ID or PhoneNumberId)
            return $"{baseUrl}/{pathId}/messages";
        }

        private async Task<WaSendResult> PostAsync(object payload)
        {
            var pathId = ResolvePathIdOrNull();
            if (string.IsNullOrWhiteSpace(pathId))
            {
                const string err = "Pinnacle: PhoneNumberId or WabaId is required.";
                _logger.LogError(err);
                return new WaSendResult(
                    Success: false,
                    Provider: "Pinnacle",
                    ProviderMessageId: null,
                    StatusCode: null,
                    RawResponse: null,
                    Error: err
                );
            }

            var url = BuildUrl(pathId);
            var json = JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // Pinbot requires apikey header (no Bearer token)
            if (!string.IsNullOrWhiteSpace(_setting.ApiKey))
            {
                req.Headers.TryAddWithoutValidation("apikey", _setting.ApiKey);
            }
            else
            {
                _logger.LogWarning("PinbotProvider: ApiKey is empty for BusinessId {BusinessId}", _setting.BusinessId);
            }

            var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Pinbot send failed (HTTP {Status}): {Body}", (int)res.StatusCode, body);
                return new WaSendResult(
                    Success: false,
                    Provider: "Pinbot",
                    ProviderMessageId: null,
                    StatusCode: res.StatusCode,
                    RawResponse: body,
                    Error: res.ReasonPhrase
                );
            }

            string? id = null;
            try
            {
                // Pinbot often mirrors Meta's envelope, but be defensive.
                var root = JsonNode.Parse(body);
                id = root?["messages"]?[0]?["id"]?.GetValue<string>()
                     ?? root?["message"]?["id"]?.GetValue<string>();
            }
            catch
            {
                // keep raw; ID remains null
            }

            return new WaSendResult(
                Success: true,
                Provider: "Pinbot",
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
                text = new { body }
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
                    language = new { code = languageCode },
                    components
                }
            });

        public Task<WaSendResult> SendInteractiveAsync(object fullPayload)
            => PostAsync(fullPayload);
    }
}
