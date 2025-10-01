using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using xbytechat.api.Features.Webhooks.Directory;            // ✅ provider directory
using xbytechat.api.Features.Webhooks.Pinnacle.Services.Adapters;
using xbytechat.api.Features.Webhooks.Services.Processors;
using xbytechat_api.Features.Billing.Services;

namespace xbytechat.api.Features.Webhooks.Services
{
    /// <summary>
    /// Central dispatcher for WhatsApp webhook events.
    /// Routes payloads to the appropriate processor based on payload type.
    /// </summary>
    public class WhatsAppWebhookDispatcher : IWhatsAppWebhookDispatcher
    {
        private readonly IStatusWebhookProcessor _statusProcessor;           // legacy fallback (keep)
        private readonly ITemplateWebhookProcessor _templateProcessor;       // template events path (unchanged)
        private readonly IClickWebhookProcessor _clickProcessor;             // click/journey path (unchanged)
        private readonly IInboundMessageProcessor _inboundMessageProcessor;  // inbound chat path (unchanged)
        private readonly IWhatsAppWebhookService _webhookService;            // ✅ for new unified status updater call
        private readonly IProviderDirectory _directory;                      // ✅ resolve BusinessId from provider hints
        private readonly ILogger<WhatsAppWebhookDispatcher> _logger;
        private readonly IPinnacleToMetaAdapter _pinnacleToMetaAdapter;
        private readonly IBillingIngestService _billingIngest;
        public WhatsAppWebhookDispatcher(
            IStatusWebhookProcessor statusProcessor,
            ITemplateWebhookProcessor templateProcessor,
            ILogger<WhatsAppWebhookDispatcher> logger,
            IClickWebhookProcessor clickProcessor,
            IInboundMessageProcessor inboundMessageProcessor,
            IWhatsAppWebhookService webhookService,     // ✅ add
            IProviderDirectory directory,
            IPinnacleToMetaAdapter pinnacleToMetaAdapter,
            IBillingIngestService billingIngest
        // ✅ add
        )
        {
            _statusProcessor = statusProcessor;
            _templateProcessor = templateProcessor;
            _logger = logger;
            _clickProcessor = clickProcessor;
            _inboundMessageProcessor = inboundMessageProcessor;
            _webhookService = webhookService;
            _directory = directory;
            _pinnacleToMetaAdapter = pinnacleToMetaAdapter;
            _billingIngest = billingIngest;
        }

        public async Task DispatchAsync(JsonElement payload)
        {
            _logger.LogWarning("📦 Dispatcher Raw Payload:\n{Payload}", payload.ToString());

            try
            {
                // 0) Detect provider & normalize to a Meta-like envelope for downstream processors
                var provider = DetectProvider(payload); // "meta" | "pinnacle" | null

                JsonElement envelope = provider == "pinnacle"
                    ? _pinnacleToMetaAdapter.ToMetaEnvelope(payload)
                    : payload;

                if (!envelope.TryGetProperty("entry", out var entries)) return;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes)) continue;

                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value)) continue;

                        // 1) STATUS UPDATES
                        if (IsStatusPayload(envelope)) // 🔁 use envelope, not raw payload
                        {
                            Guid? businessId = null;
                            try
                            {
                                var hints = ExtractNumberHints(envelope, provider); // 🔁 from envelope
                                businessId = await _directory.ResolveBusinessIdAsync(
                                    provider: provider,
                                    phoneNumberId: hints.PhoneNumberId,
                                    displayPhoneNumber: hints.DisplayPhoneNumber,
                                    wabaId: hints.WabaId,
                                    waId: hints.WaId
                                );
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "ProviderDirectory lookup failed; will fallback to legacy status processor.");
                            }

                            if (businessId is Guid bid && !string.IsNullOrWhiteSpace(provider))
                            {
                                _logger.LogInformation("📦 Routing to Unified Status Updater (provider={Provider}, businessId={BusinessId})", provider, bid);
                                // 🔹 NEW: billing ingest (captures pricing/conversation updates that arrive in status webhooks)
                                var providerCanonical = (provider ?? "meta").Equals("pinnacle", StringComparison.OrdinalIgnoreCase)
                                    ? "PINNACLE"
                                    : "META_CLOUD";

                                await _billingIngest.IngestFromWebhookAsync(bid, providerCanonical, envelope.ToString());


                                await _webhookService.ProcessStatusUpdateAsync(bid, provider!, envelope); // 🔁 pass envelope

                            }
                            else
                            {
                                _logger.LogWarning("⚠️ Status routing fallback → legacy processor (provider={Provider}, businessId={BusinessId})", provider, businessId);
                                await _statusProcessor.ProcessStatusUpdateAsync(envelope); // 🔁 pass envelope
                            }
                            continue;
                        }

                        // 2) TEMPLATE EVENTS (unchanged)
                        if (value.TryGetProperty("event", out var eventType)
                            && eventType.GetString()?.StartsWith("template_") == true)
                        {
                            _logger.LogInformation("📦 Routing to Template Processor");
                            await _templateProcessor.ProcessTemplateUpdateAsync(envelope); // 🔁 pass envelope
                            continue;
                        }

                        // 3) MESSAGES (clicks + inbound)
                        if (!value.TryGetProperty("messages", out var msgs) || msgs.GetArrayLength() == 0)
                        {
                            _logger.LogDebug("ℹ️ No 'messages' array present.");
                            continue;
                        }

                        foreach (var m in msgs.EnumerateArray())
                        {
                            if (!m.TryGetProperty("type", out var typeProp))
                            {
                                _logger.LogDebug("ℹ️ Message without 'type' field.");
                                continue;
                            }

                            var type = typeProp.GetString();

                            // (A) Legacy quick-reply button → CLICK
                            if (type == "button")
                            {
                                _logger.LogInformation("👉 Routing to Click Processor (legacy 'button')");
                                await _clickProcessor.ProcessClickAsync(change.GetProperty("value")); // 🔁 from envelope
                                continue;
                            }

                            // (B) Interactive (button_reply / list_reply) → CLICK
                            if (type == "interactive" && m.TryGetProperty("interactive", out var interactive))
                            {
                                if (interactive.TryGetProperty("type", out var interactiveType) &&
                                    interactiveType.GetString() == "button_reply")
                                {
                                    _logger.LogInformation("👉 Routing to Click Processor (interactive/button_reply)");
                                    await _clickProcessor.ProcessClickAsync(change.GetProperty("value")); // 🔁 from envelope
                                    continue;
                                }

                                if (interactive.TryGetProperty("list_reply", out _))
                                {
                                    _logger.LogInformation("👉 Routing to Click Processor (interactive/list_reply)");
                                    await _clickProcessor.ProcessClickAsync(change.GetProperty("value")); // 🔁 from envelope
                                    continue;
                                }
                            }

                            // (C) Inbound plain message types → INBOUND
                            if (type is "text" or "image" or "audio")
                            {
                                _logger.LogInformation("💬 Routing to InboundMessageProcessor (type: {Type})", type);
                                await _inboundMessageProcessor.ProcessChatAsync(change.GetProperty("value")); // 🔁 from envelope
                                continue;
                            }

                            _logger.LogDebug("ℹ️ Message type '{Type}' not handled by dispatcher.", type);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Dispatcher failed to process WhatsApp webhook.");
            }
        }

        private static bool IsStatusPayload(JsonElement root)
        {
            // Try Meta shape first: entry[].changes[].value.statuses
            if (TryGetMetaValue(root, out var val) && val.Value.TryGetProperty("statuses", out _))
                return true;

            // Try common Pinnacle shapes: "status" or event containing "status"
            if (root.TryGetProperty("status", out _)) return true;
            if (root.TryGetProperty("event", out var ev) &&
                (ev.GetString()?.Contains("status", StringComparison.OrdinalIgnoreCase) ?? false))
                return true;

            return false;
        }

        private static string? DetectProvider(JsonElement root)
        {
            // Heuristics by envelope
            if (root.TryGetProperty("object", out var obj) && obj.GetString() == "whatsapp_business_account")
                return "meta";
            if (root.TryGetProperty("entry", out _))
                return "meta";
            if (root.TryGetProperty("event", out _))
                return "pinnacle";

            return null;
        }
        private static bool TryGetMetaValue(JsonElement root, out (JsonElement Value, JsonElement? Change, JsonElement? Entry) res)
        {
            res = default;
            if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
                return false;

            var entry = entries[0];
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() == 0)
                return false;

            var change = changes[0];
            if (!change.TryGetProperty("value", out var value))
                return false;

            res = (value, change, entry);
            return true;
        }

        private static NumberHints ExtractNumberHints(JsonElement root, string? provider)
        {
            var hints = new NumberHints();

            if (string.Equals(provider, "meta", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetMetaValue(root, out var v))
                {
                    if (v.Value.TryGetProperty("metadata", out var md))
                    {
                        if (md.TryGetProperty("phone_number_id", out var pnid))
                            hints.PhoneNumberId = pnid.GetString();

                        if (md.TryGetProperty("display_phone_number", out var disp))
                            hints.DisplayPhoneNumber = NormalizePhone(disp.GetString());
                    }

                    if (v.Value.TryGetProperty("statuses", out var statuses) &&
                        statuses.ValueKind == JsonValueKind.Array && statuses.GetArrayLength() > 0)
                    {
                        var s0 = statuses[0];
                        if (s0.TryGetProperty("recipient_id", out var rid))
                            hints.WaId = rid.GetString();
                    }
                }
            }
            else if (string.Equals(provider, "pinnacle", StringComparison.OrdinalIgnoreCase))
            {
                // Adjust to your Pinnacle adapter payload (post-adaptation).
                // If you inject phone_number_id when adapting to Meta shape, this will pick it up:
                if (root.TryGetProperty("phone_number_id", out var pn))
                    hints.PhoneNumberId = pn.GetString();

                // Fallback to sender number fields:
                if (root.TryGetProperty("from", out var from))
                    hints.DisplayPhoneNumber = NormalizePhone(from.GetString());
                else if (root.TryGetProperty("msisdn", out var msisdn))
                    hints.DisplayPhoneNumber = NormalizePhone(msisdn.GetString());

                if (root.TryGetProperty("wabaId", out var waba))
                    hints.WabaId = waba.GetString();
            }

            return hints;
        }

        private static string? NormalizePhone(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var t = v.Trim();
            var keepPlus = t.StartsWith("+");
            var digits = new string(t.Where(char.IsDigit).ToArray());
            return keepPlus ? "+" + digits : digits;
        }

        private struct NumberHints
        {
            public string? PhoneNumberId { get; set; }
            public string? DisplayPhoneNumber { get; set; }
            public string? WabaId { get; set; }
            public string? WaId { get; set; }
        }
    }
}



