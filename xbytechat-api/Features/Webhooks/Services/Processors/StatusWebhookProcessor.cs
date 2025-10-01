using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading.Tasks;

// 👇 where your AppDbContext lives
using xbytechat.api;

using xbytechat.api.Features.CampaignTracking.Models;   // CampaignSendLog
using xbytechat.api.Features.MessageManagement.DTOs;    // MessageLog
using xbytechat.api.Features.Webhooks.Services.Resolvers;

namespace xbytechat.api.Features.Webhooks.Services.Processors
{
    /// <summary>
    /// Legacy status processor (back-compat).
    /// - Extracts statuses from the payload
    /// - Resolves CampaignSendLog via IMessageIdResolver when possible
    /// - Updates CampaignSendLog / MessageLog idempotently
    /// New provider-aware flow should go through the dispatcher -> WhatsAppWebhookService.
    /// </summary>
    public class StatusWebhookProcessor : IStatusWebhookProcessor
    {
        private readonly AppDbContext _context;
        private readonly ILogger<StatusWebhookProcessor> _logger;
        private readonly IMessageIdResolver _messageIdResolver;

        public StatusWebhookProcessor(
            AppDbContext context,
            ILogger<StatusWebhookProcessor> logger,
            IMessageIdResolver messageIdResolver)
        {
            _context = context;
            _logger = logger;
            _messageIdResolver = messageIdResolver;
        }

        /// <summary>
        /// Entry point from dispatcher (legacy path).
        /// Normalizes Meta envelope to a "value" object, then processes.
        /// </summary>
        public async Task ProcessStatusUpdateAsync(JsonElement payload)
        {
            _logger.LogDebug("status_webhook_in (legacy) \n{Payload}", payload.ToString());

            // 1) Envelope → value
            if (TryExtractValue(payload, out var value))
            {
                await ProcessAsync(value);
                return;
            }

            // 2) Already value-like (adapter flattened)
            if (payload.ValueKind == JsonValueKind.Object &&
                (payload.TryGetProperty("statuses", out _) || payload.TryGetProperty("messages", out _)))
            {
                await ProcessAsync(payload);
                return;
            }

            _logger.LogWarning("Unrecognized status payload shape (legacy path).");
        }

        /// <summary>
        /// Extract statuses from a Meta-like "value" object and update DB.
        /// </summary>
        public async Task ProcessAsync(JsonElement value)
        {
            if (!value.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("⚠️ 'statuses' array missing in webhook payload (legacy path).");
                return;
            }

            foreach (var status in statuses.EnumerateArray())
            {
                if (status.ValueKind != JsonValueKind.Object) continue;

                // message id (WAMID)
                var messageId = status.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;

                // status text
                var statusText = status.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
                    ? stEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(statusText))
                {
                    _logger.LogWarning("⚠️ Missing messageId or status in webhook payload (legacy path).");
                    continue;
                }

                // timestamp (string or number)
                DateTime? eventTime = null;
                if (status.TryGetProperty("timestamp", out var tsEl))
                {
                    if (tsEl.ValueKind == JsonValueKind.String && long.TryParse(tsEl.GetString(), out var epochS))
                        eventTime = DateTimeOffset.FromUnixTimeSeconds(epochS).UtcDateTime;
                    else if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var epochN))
                        eventTime = DateTimeOffset.FromUnixTimeSeconds(epochN).UtcDateTime;
                }

                _logger.LogDebug("🕓 Parsed timestamp: {Time} (raw kind={Kind})",
                    eventTime?.ToString("o") ?? "n/a", status.TryGetProperty("timestamp", out var tsDbg) ? tsDbg.ValueKind.ToString() : "n/a");

                // ✅ First try resolving a CampaignSendLog row via resolver
                Guid? sendLogId = null;
                try
                {
                    sendLogId = await _messageIdResolver.ResolveCampaignSendLogIdAsync(messageId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MessageId resolver failed for {MessageId}", messageId);
                }

                if (sendLogId is Guid sid)
                {
                    var log = await _context.Set<CampaignSendLog>()
                                            .FirstOrDefaultAsync(l => l.Id == sid);

                    if (log != null)
                    {
                        bool changed = false;

                        var newStatus = MapMetaStatus(statusText);
                        if (!string.IsNullOrEmpty(newStatus) &&
                            !string.Equals(log.SendStatus, newStatus, StringComparison.Ordinal))
                        {
                            log.SendStatus = newStatus;
                            changed = true;
                        }

                        if (statusText == "sent" && (log.SentAt == null || log.SentAt == default) && eventTime.HasValue)
                        {
                            log.SentAt = eventTime.Value;
                            changed = true;
                        }
                        if (statusText == "delivered" && (log.DeliveredAt == null || log.DeliveredAt == default) && eventTime.HasValue)
                        {
                            log.DeliveredAt = eventTime.Value;
                            changed = true;
                        }
                        if (statusText == "read" && (log.ReadAt == null || log.ReadAt == default) && eventTime.HasValue)
                        {
                            log.ReadAt = eventTime.Value;
                            changed = true;
                        }

                        if (changed)
                        {
                            await _context.SaveChangesAsync();
                            _logger.LogInformation("✅ CampaignSendLog updated (legacy) for MessageId: {MessageId} → {Status}", messageId, newStatus ?? statusText);
                        }
                        else
                        {
                            _logger.LogInformation("🔁 Duplicate status '{Status}' skipped for MessageId: {MessageId} (legacy)", statusText, messageId);
                        }

                        continue; // done with this status item
                    }
                }

                // 🔁 Fallback: update MessageLog when there’s no CampaignSendLog
                var msg = await _context.Set<MessageLog>()
                                        .FirstOrDefaultAsync(m => m.MessageId == messageId);

                if (msg != null)
                {
                    bool changed = false;

                    switch (statusText)
                    {
                        case "sent":
                            if (!EqualsIgnoreCase(msg.Status, "Sent"))
                            {
                                msg.Status = "Sent";
                                changed = true;
                            }
                            if ((msg.SentAt == null || msg.SentAt == default) && eventTime.HasValue)
                            {
                                msg.SentAt = eventTime.Value;
                                changed = true;
                            }
                            break;

                        case "delivered":
                            // no DeliveredAt column on MessageLog; just progression
                            if (!EqualsIgnoreCase(msg.Status, "Read") &&
                                !EqualsIgnoreCase(msg.Status, "Delivered"))
                            {
                                msg.Status = "Delivered";
                                changed = true;
                            }
                            if ((msg.SentAt == null || msg.SentAt == default) && eventTime.HasValue)
                            {
                                msg.SentAt = eventTime.Value; // ensure SentAt eventually set
                                changed = true;
                            }
                            break;

                        case "read":
                            if (!EqualsIgnoreCase(msg.Status, "Read"))
                            {
                                msg.Status = "Read";
                                changed = true;
                            }
                            if ((msg.SentAt == null || msg.SentAt == default) && eventTime.HasValue)
                            {
                                msg.SentAt = eventTime.Value;
                                changed = true;
                            }
                            break;

                        default:
                            // leave as-is for unknown statuses
                            break;
                    }

                    if (changed)
                    {
                        await _context.SaveChangesAsync();
                        _logger.LogInformation("ℹ️ MessageLog updated (legacy) for MessageId: {MessageId} → {Status}", messageId, msg.Status);
                    }
                    else
                    {
                        _logger.LogInformation("🔁 Duplicate status '{Status}' skipped for MessageId: {MessageId} (legacy)", statusText, messageId);
                    }
                }
                else
                {
                    // lower severity; common when a send failed before obtaining a message id
                    _logger.LogInformation("ⓘ No matching CampaignSendLog/MessageLog for MessageId: {MessageId} (legacy)", messageId);
                }
            }
        }

        // ----------------- helpers -----------------

        private static bool TryExtractValue(JsonElement payload, out JsonElement value)
        {
            value = default;
            if (payload.ValueKind != JsonValueKind.Object) return false;
            if (!payload.TryGetProperty("entry", out var entry) || entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() == 0) return false;

            var e0 = entry[0];
            if (!e0.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() == 0) return false;

            var c0 = changes[0];
            if (!c0.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object) return false;

            value = v;
            return true;
        }

        private static string? MapMetaStatus(string? s) =>
            (s ?? "").ToLowerInvariant() switch
            {
                "sent" => "Sent",
                "delivered" => "Delivered",
                "read" => "Read",
                "failed" => "Failed",
                "deleted" => "Deleted",
                _ => null
            };

        private static bool EqualsIgnoreCase(string? a, string? b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}


