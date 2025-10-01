using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using xbytechat.api;                                      // AppDbContext
using xbytechat.api.Features.CampaignTracking.Models;     // CampaignSendLog
using xbytechat.api.Features.Webhooks.Status;             // IMessageStatusUpdater, StatusEvent, MessageDeliveryState

namespace xbytechat.api.Features.Webhooks.Services
{
    public class WhatsAppWebhookService : IWhatsAppWebhookService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<WhatsAppWebhookService> _logger;
        private readonly IMessageStatusUpdater _updater;

        public WhatsAppWebhookService(
            AppDbContext context,
            ILogger<WhatsAppWebhookService> logger,
            IMessageStatusUpdater updater)
        {
            _context = context;
            _logger = logger;
            _updater = updater;
        }

        /// <summary>
        /// Legacy path: payload only (Meta-like).
        /// We keep this for back-compat, but we *upgrade* behavior:
        /// - For each status: find CampaignSendLog by MessageId
        /// - If found → get BusinessId and delegate to the unified updater
        /// - If not found → keep legacy log-only update (minimal)
        /// </summary>
        public async Task ProcessStatusUpdateAsync(JsonElement payload, CancellationToken ct = default)
        {
            _logger.LogInformation("📦 Processing Webhook Status (legacy):\n{Pretty}",
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

            if (!payload.TryGetProperty("entry", out var entries))
            {
                _logger.LogWarning("⚠️ Payload missing 'entry' property.");
                return;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes)) continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value)) continue;
                    if (!value.TryGetProperty("statuses", out var statuses)) continue;

                    foreach (var status in statuses.EnumerateArray())
                    {
                        string? messageId = status.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        string? statusText = status.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;

                        // timestamp may be string or number
                        long ts = 0;
                        if (status.TryGetProperty("timestamp", out var tsProp))
                        {
                            if (tsProp.ValueKind == JsonValueKind.String && long.TryParse(tsProp.GetString(), out var parsedTs))
                                ts = parsedTs;
                            else if (tsProp.ValueKind == JsonValueKind.Number)
                                ts = tsProp.GetInt64();
                        }

                        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(statusText))
                        {
                            _logger.LogWarning("⚠️ Missing messageId or status in webhook payload.");
                            continue;
                        }

                        // Try to locate CampaignSendLog (gives us BusinessId)
                        var sendLog = await _context.Set<CampaignSendLog>()
                            .FirstOrDefaultAsync(l => l.MessageId == messageId, ct);

                        if (sendLog != null)
                        {
                            var ev = new StatusEvent
                            {
                                BusinessId = sendLog.BusinessId,
                                Provider = "meta", // legacy path is Meta-shaped; adjust if you also send Pinnacle here
                                ProviderMessageId = messageId,
                                State = MapMetaState(statusText),
                                OccurredAt = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts) : DateTimeOffset.UtcNow
                            };

                            await _updater.UpdateAsync(ev, ct);
                            _logger.LogInformation("✅ Unified update applied for MessageId {MessageId} (state={State})", messageId, statusText);
                        }
                        else
                        {
                            // Fallback: minimal legacy update to CampaignSendLogs (kept from your original code)
                            var time = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : (DateTime?)null;

                            var log = await _context.Set<CampaignSendLog>()
                                .FirstOrDefaultAsync(l => l.MessageId == messageId, ct);

                            if (log == null)
                            {
                                _logger.LogWarning("⚠️ No matching CampaignSendLog for MessageId: {MessageId}", messageId);
                                continue;
                            }

                            var newStatus = statusText switch
                            {
                                "sent" => "Sent",
                                "delivered" => "Delivered",
                                "read" => "Read",
                                _ => null
                            };

                            bool isUpdated = false;

                            if (!string.IsNullOrEmpty(newStatus) && !string.Equals(log.SendStatus, newStatus, StringComparison.Ordinal))
                            {
                                log.SendStatus = newStatus;
                                isUpdated = true;
                            }

                            if (statusText == "delivered" && log.DeliveredAt == null && time.HasValue)
                            {
                                log.DeliveredAt = time.Value;
                                isUpdated = true;
                            }

                            if (statusText == "read" && log.ReadAt == null && time.HasValue)
                            {
                                log.ReadAt = time.Value;
                                isUpdated = true;
                            }

                            if (isUpdated)
                            {
                                await _context.SaveChangesAsync(ct);
                                _logger.LogInformation("✅ Log updated for MessageId: {MessageId} → {Status}", messageId, newStatus);
                            }
                            else
                            {
                                _logger.LogInformation("🔁 Duplicate status '{Status}' skipped for MessageId: {MessageId}", statusText, messageId);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// New provider-aware overload called by the dispatcher after it resolves BusinessId + Provider.
        /// Always uses the unified updater.
        /// </summary>
        public async Task ProcessStatusUpdateAsync(Guid businessId, string provider, JsonElement payload, CancellationToken ct = default)
        {
            provider = (provider ?? "").Trim().ToLowerInvariant();

            foreach (var ev in ParseStatusEvents(businessId, provider, payload))
            {
                await _updater.UpdateAsync(ev, ct);
            }
        }

        // ---------------- Parsers (Meta + Pinnacle) ----------------

        private static IEnumerable<StatusEvent> ParseStatusEvents(Guid businessId, string provider, JsonElement root)
        {
            if (provider == "meta" || provider == "meta_cloud" || provider == "meta-cloud")
            {
                if (TryGetMetaValue(root, out var v) &&
                    v.Value.TryGetProperty("statuses", out var statuses) &&
                    statuses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in statuses.EnumerateArray())
                    {
                        var stateStr = s.TryGetProperty("status", out var st) ? st.GetString() : null;
                        var state = MapMetaState(stateStr);

                        var tsStr = s.TryGetProperty("timestamp", out var tsv) ? tsv.ToString() : null;
                        var occurredAt = TryParseUnix(tsStr) ?? DateTimeOffset.UtcNow;

                        var providerMsgId = s.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                        var waId = s.TryGetProperty("recipient_id", out var rid) ? rid.GetString() : null;

                        string? errorCode = null, errorMsg = null;
                        if (s.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                        {
                            var e0 = errs[0];
                            if (e0.TryGetProperty("code", out var cv)) errorCode = cv.ToString();
                            if (e0.TryGetProperty("message", out var mv)) errorMsg = mv.GetString();
                        }

                        string? conversationId = null;
                        if (s.TryGetProperty("conversation", out var conv) && conv.TryGetProperty("id", out var cid))
                            conversationId = cid.GetString();

                        yield return new StatusEvent
                        {
                            BusinessId = businessId,
                            Provider = "meta",
                            ProviderMessageId = providerMsgId ?? string.Empty,
                            RecipientWaId = waId,
                            State = state,
                            OccurredAt = occurredAt,
                            ErrorCode = errorCode,
                            ErrorMessage = errorMsg,
                            ConversationId = conversationId
                        };
                    }
                }
                yield break;
            }

            if (provider == "pinnacle")
            {
                // Support both object and array shapes
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var ev in ParsePinnacleObject(businessId, root))
                        yield return ev;
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                        foreach (var ev in ParsePinnacleObject(businessId, item))
                            yield return ev;
                }
                yield break;
            }
        }

        private static IEnumerable<StatusEvent> ParsePinnacleObject(Guid businessId, JsonElement obj)
        {
            var providerMsgId = obj.TryGetProperty("message_id", out var mid) ? mid.GetString()
                              : obj.TryGetProperty("id", out var idv) ? idv.GetString()
                              : null;

            var waId = obj.TryGetProperty("to", out var to) ? to.GetString()
                     : obj.TryGetProperty("recipient_id", out var rid) ? rid.GetString()
                     : null;

            var stateStr = obj.TryGetProperty("status", out var st) ? st.GetString()
                         : obj.TryGetProperty("event", out var ev) ? ev.GetString()
                         : null;

            var state = MapPinnacleState(stateStr);

            var tsStr = obj.TryGetProperty("timestamp", out var tsv) ? tsv.ToString() : null;
            var occurredAt = TryParseUnix(tsStr) ?? DateTimeOffset.UtcNow;

            string? errorCode = null, errorMsg = null;
            if (obj.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("code", out var cv)) errorCode = cv.ToString();
                if (err.TryGetProperty("message", out var mv)) errorMsg = mv.GetString();
            }

            yield return new StatusEvent
            {
                BusinessId = businessId,
                Provider = "pinnacle",
                ProviderMessageId = providerMsgId ?? string.Empty,
                RecipientWaId = waId,
                State = state,
                OccurredAt = occurredAt,
                ErrorCode = errorCode,
                ErrorMessage = errorMsg
            };
        }

        // ---------------- helpers ----------------

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

        private static MessageDeliveryState MapMetaState(string? s) =>
            (s ?? "").ToLowerInvariant() switch
            {
                "sent" => MessageDeliveryState.Sent,
                "delivered" => MessageDeliveryState.Delivered,
                "read" => MessageDeliveryState.Read,
                "failed" => MessageDeliveryState.Failed,
                "deleted" => MessageDeliveryState.Deleted,
                _ => MessageDeliveryState.Sent
            };

        private static MessageDeliveryState MapPinnacleState(string? s)
        {
            var v = (s ?? "").ToLowerInvariant();
            if (v.Contains("deliv")) return MessageDeliveryState.Delivered;
            if (v.Contains("read")) return MessageDeliveryState.Read;
            if (v.Contains("fail") || v.Contains("error")) return MessageDeliveryState.Failed;
            if (v.Contains("sent") || v.Contains("submit")) return MessageDeliveryState.Sent;
            if (v.Contains("delete")) return MessageDeliveryState.Deleted;
            return MessageDeliveryState.Sent;
        }

        private static DateTimeOffset? TryParseUnix(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return null;
            if (long.TryParse(val, out var s)) return DateTimeOffset.FromUnixTimeSeconds(s);
            return null;
        }
    }
}


