using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using xbytechat.api; // AppDbContext
using xbytechat_api.Features.Billing.Models;

namespace xbytechat_api.Features.Billing.Services
{
    public class BillingIngestService : IBillingIngestService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<BillingIngestService> _log;

        public BillingIngestService(AppDbContext db, ILogger<BillingIngestService> log)
        {
            _db = db;
            _log = log;
        }

        public async Task IngestFromSendResponseAsync(Guid businessId, Guid messageLogId, string provider, string rawResponseJson)
        {
            // Meta send usually returns only message ID; pricing lands via webhook.
            // We still extract ProviderMessageId early to link later webhook updates.
            try
            {
                using var doc = JsonDocument.Parse(rawResponseJson);
                string? providerMessageId =
                    doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0
                        ? msgs[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null
                    : doc.RootElement.TryGetProperty("id", out var idEl2) ? idEl2.GetString()
                    : null;

                var logRow = await _db.MessageLogs.FirstOrDefaultAsync(x => x.Id == messageLogId && x.BusinessId == businessId);
                if (logRow != null)
                {
                    logRow.Provider = provider;
                    if (!string.IsNullOrWhiteSpace(providerMessageId))
                        logRow.ProviderMessageId = providerMessageId;
                }

                // Store audit event
                var ev = new ProviderBillingEvent
                {
                    BusinessId = businessId,
                    MessageLogId = messageLogId,
                    Provider = provider,
                    EventType = "send_response",
                    ProviderMessageId = providerMessageId,
                    PayloadJson = rawResponseJson,
                    OccurredAt = DateTimeOffset.UtcNow
                };
                _db.ProviderBillingEvents.Add(ev);

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to ingest send response payload for business {biz}", businessId);
            }
        }

        public async Task IngestFromWebhookAsync(Guid businessId, string provider, string payloadJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                var now = DateTimeOffset.UtcNow;

                // Local idempotency guard:
                // Consider an event "existing" if (BusinessId, Provider, EventType)
                // matches and we have either the same ProviderMessageId OR (when absent) the same ConversationId.
                Task<bool> ExistsAsync(string eventType, string? providerMessageId, string? conversationId)
                {
                    if (!string.IsNullOrWhiteSpace(providerMessageId))
                    {
                        return _db.ProviderBillingEvents.AsNoTracking().AnyAsync(x =>
                            x.BusinessId == businessId &&
                            x.Provider == provider &&
                            x.EventType == eventType &&
                            x.ProviderMessageId == providerMessageId);
                    }

                    if (!string.IsNullOrWhiteSpace(conversationId))
                    {
                        return _db.ProviderBillingEvents.AsNoTracking().AnyAsync(x =>
                            x.BusinessId == businessId &&
                            x.Provider == provider &&
                            x.EventType == eventType &&
                            x.ConversationId == conversationId);
                    }

                    // No natural key available; let it through (DB unique index can still protect if present).
                    return Task.FromResult(false);
                }

                if (string.Equals(provider, "META_CLOUD", StringComparison.OrdinalIgnoreCase))
                {
                    // Typical Meta structure:
                    // entry[].changes[].value.statuses[] with:
                    //  - id (wamid)
                    //  - status (sent / delivered / read / etc.)
                    //  - timestamp (unix seconds, string or number)
                    //  - conversation { id, expiration_timestamp }
                    //  - pricing { billable, category, amount, currency }
                    foreach (var entry in Enumerate(doc.RootElement, "entry"))
                        foreach (var change in Enumerate(entry, "changes"))
                        {
                            if (!change.TryGetProperty("value", out var value)) continue;

                            foreach (var st in Enumerate(value, "statuses"))
                            {
                                string? providerMessageId = st.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                                string? status = null;
                                if (st.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                                    status = statusEl.GetString()?.ToLowerInvariant();

                                // OccurredAt from provider if present
                                DateTimeOffset occurredAt = now;
                                if (st.TryGetProperty("timestamp", out var tsEl))
                                {
                                    if (tsEl.ValueKind == JsonValueKind.String && long.TryParse(tsEl.GetString(), out var tsLong))
                                        occurredAt = DateTimeOffset.FromUnixTimeSeconds(tsLong);
                                    else if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var tsNum))
                                        occurredAt = DateTimeOffset.FromUnixTimeSeconds(tsNum);
                                }

                                // Conversation info (Meta expires 24h after start)
                                string? conversationId = null;
                                DateTimeOffset? convStartedAt = null;
                                if (st.TryGetProperty("conversation", out var convEl) && convEl.ValueKind == JsonValueKind.Object)
                                {
                                    if (convEl.TryGetProperty("id", out var cidEl)) conversationId = cidEl.GetString();

                                    if (convEl.TryGetProperty("expiration_timestamp", out var expEl))
                                    {
                                        long exp;
                                        if (expEl.ValueKind == JsonValueKind.String && long.TryParse(expEl.GetString(), out var expStr))
                                            exp = expStr;
                                        else if (expEl.ValueKind == JsonValueKind.Number && expEl.TryGetInt64(out var expNum))
                                            exp = expNum;
                                        else
                                            exp = 0;

                                        if (exp > 0)
                                        {
                                            var expiration = DateTimeOffset.FromUnixTimeSeconds(exp);
                                            convStartedAt = expiration.AddHours(-24);
                                        }
                                    }
                                }

                                // Pricing block (optional per status)
                                string? category = null;
                                bool? billable = null;
                                decimal? amount = null;
                                string? currency = null;

                                if (st.TryGetProperty("pricing", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
                                {
                                    if (pEl.TryGetProperty("category", out var catEl))
                                        category = catEl.GetString()?.ToLowerInvariant();

                                    if (pEl.TryGetProperty("billable", out var bilEl) &&
                                        (bilEl.ValueKind == JsonValueKind.True || bilEl.ValueKind == JsonValueKind.False))
                                        billable = bilEl.GetBoolean();

                                    if (pEl.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number)
                                        amount = amtEl.GetDecimal();

                                    if (pEl.TryGetProperty("currency", out var curEl) && curEl.ValueKind == JsonValueKind.String)
                                        currency = curEl.GetString();
                                }

                                // 1) Status event (sent/delivered/read...) — write once
                                if (!string.IsNullOrWhiteSpace(status))
                                {
                                    var statusEventType = status; // store status as EventType
                                    if (!await ExistsAsync(statusEventType, providerMessageId, conversationId))
                                    {
                                        _db.ProviderBillingEvents.Add(new ProviderBillingEvent
                                        {
                                            BusinessId = businessId,
                                            Provider = provider,
                                            EventType = statusEventType,
                                            ProviderMessageId = providerMessageId,
                                            ConversationId = conversationId,
                                            ConversationCategory = category,
                                            IsChargeable = billable,
                                            PriceAmount = amount,
                                            PriceCurrency = currency,
                                            PayloadJson = payloadJson,
                                            OccurredAt = occurredAt
                                        });
                                    }
                                }

                                // 2) Pricing update — write once
                                bool hasAnyPricing = !string.IsNullOrWhiteSpace(category) || billable.HasValue || amount.HasValue || !string.IsNullOrWhiteSpace(currency);
                                if (hasAnyPricing && !await ExistsAsync("pricing_update", providerMessageId, conversationId))
                                {
                                    _db.ProviderBillingEvents.Add(new ProviderBillingEvent
                                    {
                                        BusinessId = businessId,
                                        Provider = provider,
                                        EventType = "pricing_update",
                                        ProviderMessageId = providerMessageId,
                                        ConversationId = conversationId,
                                        ConversationCategory = category,
                                        IsChargeable = billable,
                                        PriceAmount = amount,
                                        PriceCurrency = currency,
                                        PayloadJson = payloadJson,
                                        OccurredAt = occurredAt
                                    });
                                }

                                // Keep MessageLog in sync (when linkable)
                                var logRow = await FindMatchingMessageLog(businessId, providerMessageId, conversationId);
                                if (logRow != null)
                                {
                                    logRow.Provider = provider;
                                    if (!string.IsNullOrWhiteSpace(providerMessageId))
                                        logRow.ProviderMessageId = providerMessageId;
                                    if (!string.IsNullOrWhiteSpace(conversationId))
                                        logRow.ConversationId = conversationId;
                                    if (convStartedAt.HasValue)
                                        logRow.ConversationStartedAt = convStartedAt;

                                    if (billable.HasValue) logRow.IsChargeable = billable.Value;
                                    if (!string.IsNullOrWhiteSpace(category)) logRow.ConversationCategory = category;
                                    if (amount.HasValue) logRow.PriceAmount = amount;
                                    if (!string.IsNullOrWhiteSpace(currency)) logRow.PriceCurrency = currency;
                                }
                            }
                        }
                }
                else if (string.Equals(provider, "PINNACLE", StringComparison.OrdinalIgnoreCase))
                {
                    // Scan for "pricing" nodes; try to infer message & conversation from parent context.
                    foreach (var pricing in JsonPathAll(doc.RootElement, "pricing"))
                    {
                        string? category = pricing.TryGetProperty("category", out var catEl) ? catEl.GetString()?.ToLowerInvariant() : null;
                        bool? billable = (pricing.TryGetProperty("billable", out var bilEl) &&
                                          (bilEl.ValueKind == JsonValueKind.True || bilEl.ValueKind == JsonValueKind.False))
                                          ? bilEl.GetBoolean() : (bool?)null;

                        decimal? amount = null;
                        if (pricing.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number)
                            amount = amtEl.GetDecimal();

                        string? currency = pricing.TryGetProperty("currency", out var curEl) ? curEl.GetString() : null;

                        var parent = TryGetParentObject(doc.RootElement, pricing);
                        string? providerMessageId = TryGetString(parent, "id")
                                                 ?? TryGetString(parent, "message_id")
                                                 ?? TryGetString(parent, "wamid");
                        string? conversationId = TryGetString(parent, "conversation_id")
                                               ?? TryGetNestedString(parent, "conversation", "id");

                        // Optional status in same parent
                        string? status = TryGetString(parent, "status")?.ToLowerInvariant();

                        // Pricing (deduped)
                        if (!await ExistsAsync("pricing_update", providerMessageId, conversationId))
                        {
                            _db.ProviderBillingEvents.Add(new ProviderBillingEvent
                            {
                                BusinessId = businessId,
                                Provider = provider,
                                EventType = "pricing_update",
                                ProviderMessageId = providerMessageId,
                                ConversationId = conversationId,
                                ConversationCategory = category,
                                IsChargeable = billable,
                                PriceAmount = amount,
                                PriceCurrency = currency,
                                PayloadJson = payloadJson,
                                OccurredAt = now
                            });
                        }

                        // Optional status (deduped)
                        if (!string.IsNullOrWhiteSpace(status) && !await ExistsAsync(status, providerMessageId, conversationId))
                        {
                            _db.ProviderBillingEvents.Add(new ProviderBillingEvent
                            {
                                BusinessId = businessId,
                                Provider = provider,
                                EventType = status,
                                ProviderMessageId = providerMessageId,
                                ConversationId = conversationId,
                                ConversationCategory = category,
                                IsChargeable = billable,
                                PriceAmount = amount,
                                PriceCurrency = currency,
                                PayloadJson = payloadJson,
                                OccurredAt = now
                            });
                        }

                        // Update MessageLog when linkable
                        var logRow = await FindMatchingMessageLog(businessId, providerMessageId, conversationId);
                        if (logRow != null)
                        {
                            logRow.Provider = provider;
                            if (!string.IsNullOrWhiteSpace(providerMessageId))
                                logRow.ProviderMessageId = providerMessageId;
                            if (!string.IsNullOrWhiteSpace(conversationId))
                                logRow.ConversationId = conversationId;

                            if (billable.HasValue) logRow.IsChargeable = billable.Value;
                            if (!string.IsNullOrWhiteSpace(category)) logRow.ConversationCategory = category;
                            if (amount.HasValue) logRow.PriceAmount = amount;
                            if (!string.IsNullOrWhiteSpace(currency)) logRow.PriceCurrency = currency;
                        }
                    }
                }
                else
                {
                    // Unknown provider; still store the raw event for audit (idempotency relaxed here)
                    _db.ProviderBillingEvents.Add(new ProviderBillingEvent
                    {
                        BusinessId = businessId,
                        Provider = provider,
                        EventType = "unknown_provider_webhook",
                        PayloadJson = payloadJson,
                        OccurredAt = now
                    });
                }

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to ingest webhook payload for business {biz}", businessId);
            }
        }

        // -------- helpers --------
        private async Task<MessageLog?> FindMatchingMessageLog(Guid businessId, string? providerMessageId, string? conversationId)
        {
            if (!string.IsNullOrWhiteSpace(providerMessageId))
            {
                var byMsgId = await _db.MessageLogs
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.ProviderMessageId == providerMessageId);
                if (byMsgId != null) return byMsgId;
            }

            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                var byConv = await _db.MessageLogs
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.ConversationId == conversationId);
                if (byConv != null) return byConv;
            }

            return null;
        }

        // Enumerate array property safely
        private static IEnumerable<JsonElement> Enumerate(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object) yield break;
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
            foreach (var x in arr.EnumerateArray()) yield return x;
        }

        // Breadth search for any property named `name` (unique name to avoid ambiguity)
        private static IEnumerable<JsonElement> JsonPathAll(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        yield return p.Value;

                    foreach (var x in JsonPathAll(p.Value, name))
                        yield return x;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    foreach (var x in JsonPathAll(item, name))
                        yield return x;
            }
        }

        // Very lightweight "parent" guess: look for an object in ancestry that contains the node reference (best-effort)
        private static JsonElement? TryGetParentObject(JsonElement root, JsonElement node)
        {
            // System.Text.Json doesn't expose parents. We accept best-effort by scanning objects containing 'pricing'
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (object.ReferenceEquals(p.Value, node)) return root;
                        var cand = TryGetParentObject(p.Value, node);
                        if (cand.HasValue) return cand;
                    }
                    else if (p.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in p.Value.EnumerateArray())
                        {
                            if (object.ReferenceEquals(e, node)) return root;
                            var cand = TryGetParentObject(e, node);
                            if (cand.HasValue) return cand;
                        }
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in root.EnumerateArray())
                {
                    var cand = TryGetParentObject(e, node);
                    if (cand.HasValue) return cand;
                }
            }
            return null;
        }

        private static string? TryGetString(JsonElement? obj, string name)
        {
            if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object) return null;
            return obj.Value.TryGetProperty(name, out var el) ? el.GetString() : null;
        }

        private static string? TryGetNestedString(JsonElement? obj, string name1, string name2)
        {
            if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object) return null;
            if (!obj.Value.TryGetProperty(name1, out var inner) || inner.ValueKind != JsonValueKind.Object) return null;
            return inner.TryGetProperty(name2, out var v) ? v.GetString() : null;
        }
    }
}


//using System;
//using System.Linq;
//using System.Text.Json;
//using System.Threading.Tasks;
//using System.Collections.Generic;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.Logging;
//using xbytechat.api; // AppDbContext
//using xbytechat_api.Features.Billing.Models;

//namespace xbytechat_api.Features.Billing.Services
//{
//    public class BillingIngestService : IBillingIngestService
//    {
//        private readonly AppDbContext _db;
//        private readonly ILogger<BillingIngestService> _log;

//        public BillingIngestService(AppDbContext db, ILogger<BillingIngestService> log)
//        {
//            _db = db;
//            _log = log;
//        }

//        public async Task IngestFromSendResponseAsync(Guid businessId, Guid messageLogId, string provider, string rawResponseJson)
//        {
//            // Meta send usually returns only message ID; pricing lands via webhook.
//            // We still extract ProviderMessageId early to link later webhook updates.
//            try
//            {
//                using var doc = JsonDocument.Parse(rawResponseJson);
//                string? providerMessageId =
//                    doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array && msgs.GetArrayLength() > 0
//                        ? msgs[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null
//                    : doc.RootElement.TryGetProperty("id", out var idEl2) ? idEl2.GetString()
//                    : null;

//                var logRow = await _db.MessageLogs.FirstOrDefaultAsync(x => x.Id == messageLogId && x.BusinessId == businessId);
//                if (logRow != null)
//                {
//                    logRow.Provider = provider;
//                    if (!string.IsNullOrWhiteSpace(providerMessageId))
//                        logRow.ProviderMessageId = providerMessageId;
//                }

//                // Store audit event
//                var ev = new ProviderBillingEvent
//                {
//                    BusinessId = businessId,
//                    MessageLogId = messageLogId,
//                    Provider = provider,
//                    EventType = "send_response",
//                    ProviderMessageId = providerMessageId,
//                    PayloadJson = rawResponseJson,
//                    OccurredAt = DateTimeOffset.UtcNow
//                };
//                _db.ProviderBillingEvents.Add(ev);

//                await _db.SaveChangesAsync();
//            }
//            catch (Exception ex)
//            {
//                _log.LogWarning(ex, "Failed to ingest send response payload for business {biz}", businessId);
//            }
//        }

//        public async Task IngestFromWebhookAsync(Guid businessId, string provider, string payloadJson)
//        {
//            try
//            {
//                using var doc = JsonDocument.Parse(payloadJson);
//                var now = DateTimeOffset.UtcNow;

//                if (string.Equals(provider, "META_CLOUD", StringComparison.OrdinalIgnoreCase))
//                {
//                    // Typical Meta structure:
//                    // entry[].changes[].value.statuses[] with:
//                    //  - id (wamid)
//                    //  - pricing { billable, category, amount, currency }
//                    //  - conversation { id, expiration_timestamp }
//                    foreach (var entry in Enumerate(doc.RootElement, "entry"))
//                    {
//                        foreach (var change in Enumerate(entry, "changes"))
//                        {
//                            if (!change.TryGetProperty("value", out var value)) continue;

//                            foreach (var st in Enumerate(value, "statuses"))
//                            {
//                                string? providerMessageId = st.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

//                                string? category = st.TryGetProperty("pricing", out var pEl) && pEl.ValueKind == JsonValueKind.Object
//                                    ? pEl.TryGetProperty("category", out var catEl) ? catEl.GetString()?.ToLowerInvariant() : null
//                                    : null;

//                                bool? billable = st.TryGetProperty("pricing", out var pEl2) && pEl2.ValueKind == JsonValueKind.Object
//                                    ? pEl2.TryGetProperty("billable", out var bilEl) && (bilEl.ValueKind == JsonValueKind.True || bilEl.ValueKind == JsonValueKind.False)
//                                        ? bilEl.GetBoolean() : (bool?)null
//                                    : (bool?)null;

//                                decimal? amount = null;
//                                string? currency = null;
//                                if (st.TryGetProperty("pricing", out var pEl3) && pEl3.ValueKind == JsonValueKind.Object)
//                                {
//                                    if (pEl3.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number)
//                                        amount = amtEl.GetDecimal();
//                                    if (pEl3.TryGetProperty("currency", out var curEl))
//                                        currency = curEl.GetString();
//                                }

//                                string? conversationId = null;
//                                DateTimeOffset? convStartedAt = null;
//                                if (st.TryGetProperty("conversation", out var convEl) && convEl.ValueKind == JsonValueKind.Object)
//                                {
//                                    if (convEl.TryGetProperty("id", out var cidEl))
//                                        conversationId = cidEl.GetString();

//                                    // expiration_timestamp is seconds; start time not directly given.
//                                    if (convEl.TryGetProperty("expiration_timestamp", out var expEl) && expEl.ValueKind == JsonValueKind.Number)
//                                    {
//                                        var exp = DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64());
//                                        convStartedAt = exp.AddHours(-24);
//                                    }
//                                }

//                                // Audit event
//                                var ev = new ProviderBillingEvent
//                                {
//                                    BusinessId = businessId,
//                                    Provider = provider,
//                                    EventType = "pricing_update",
//                                    ProviderMessageId = providerMessageId,
//                                    ConversationId = conversationId,
//                                    ConversationCategory = category,
//                                    IsChargeable = billable,
//                                    PriceAmount = amount,
//                                    PriceCurrency = currency,
//                                    PayloadJson = payloadJson,
//                                    OccurredAt = now
//                                };
//                                _db.ProviderBillingEvents.Add(ev);

//                                // Update MessageLog when possible
//                                var logRow = await FindMatchingMessageLog(businessId, providerMessageId, conversationId);
//                                if (logRow != null)
//                                {
//                                    logRow.Provider = provider;
//                                    if (!string.IsNullOrWhiteSpace(providerMessageId))
//                                        logRow.ProviderMessageId = providerMessageId;
//                                    if (billable.HasValue) logRow.IsChargeable = billable.Value;
//                                    if (!string.IsNullOrWhiteSpace(category)) logRow.ConversationCategory = category;
//                                    if (!string.IsNullOrWhiteSpace(conversationId)) logRow.ConversationId = conversationId;
//                                    if (amount.HasValue) logRow.PriceAmount = amount;
//                                    if (!string.IsNullOrWhiteSpace(currency)) logRow.PriceCurrency = currency;
//                                    if (convStartedAt.HasValue) logRow.ConversationStartedAt = convStartedAt;
//                                }
//                            }
//                        }
//                    }
//                }
//                else if (string.Equals(provider, "PINNACLE", StringComparison.OrdinalIgnoreCase))
//                {
//                    // Pinnacle payloads vary, but often include "message_id", "conversation" with id/category and "pricing".
//                    // We'll scan the whole tree for any "pricing" objects, and attempt nearby fields for message id and conversation.
//                    foreach (var pricing in JsonPathAll(doc.RootElement, "pricing"))
//                    {
//                        string? category = pricing.TryGetProperty("category", out var catEl) ? catEl.GetString()?.ToLowerInvariant() : null;
//                        bool? billable = pricing.TryGetProperty("billable", out var bilEl) && (bilEl.ValueKind == JsonValueKind.True || bilEl.ValueKind == JsonValueKind.False)
//                            ? bilEl.GetBoolean() : (bool?)null;

//                        decimal? amount = null;
//                        if (pricing.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number)
//                            amount = amtEl.GetDecimal();
//                        string? currency = pricing.TryGetProperty("currency", out var curEl) ? curEl.GetString() : null;

//                        // Heuristics to pick neighbors in same object
//                        var parent = TryGetParentObject(doc.RootElement, pricing);
//                        string? providerMessageId = TryGetString(parent, "id")
//                                                 ?? TryGetString(parent, "message_id")
//                                                 ?? TryGetString(parent, "wamid");
//                        string? conversationId = TryGetString(parent, "conversation_id")
//                                               ?? TryGetNestedString(parent, "conversation", "id");

//                        var ev = new ProviderBillingEvent
//                        {
//                            BusinessId = businessId,
//                            Provider = provider,
//                            EventType = "pricing_update",
//                            ProviderMessageId = providerMessageId,
//                            ConversationId = conversationId,
//                            ConversationCategory = category,
//                            IsChargeable = billable,
//                            PriceAmount = amount,
//                            PriceCurrency = currency,
//                            PayloadJson = payloadJson,
//                            OccurredAt = now
//                        };
//                        _db.ProviderBillingEvents.Add(ev);

//                        var logRow = await FindMatchingMessageLog(businessId, providerMessageId, conversationId);
//                        if (logRow != null)
//                        {
//                            logRow.Provider = provider;
//                            if (!string.IsNullOrWhiteSpace(providerMessageId))
//                                logRow.ProviderMessageId = providerMessageId;
//                            if (billable.HasValue) logRow.IsChargeable = billable.Value;
//                            if (!string.IsNullOrWhiteSpace(category)) logRow.ConversationCategory = category;
//                            if (!string.IsNullOrWhiteSpace(conversationId)) logRow.ConversationId = conversationId;
//                            if (amount.HasValue) logRow.PriceAmount = amount;
//                            if (!string.IsNullOrWhiteSpace(currency)) logRow.PriceCurrency = currency;
//                        }
//                    }
//                }
//                else
//                {
//                    // Unknown provider; still store the raw event for audit
//                    _db.ProviderBillingEvents.Add(new ProviderBillingEvent
//                    {
//                        BusinessId = businessId,
//                        Provider = provider,
//                        EventType = "unknown_provider_webhook",
//                        PayloadJson = payloadJson,
//                        OccurredAt = now
//                    });
//                }

//                await _db.SaveChangesAsync();
//            }
//            catch (Exception ex)
//            {
//                _log.LogWarning(ex, "Failed to ingest webhook payload for business {biz}", businessId);
//            }
//        }

//        // -------- helpers --------
//        private async Task<MessageLog?> FindMatchingMessageLog(Guid businessId, string? providerMessageId, string? conversationId)
//        {
//            if (!string.IsNullOrWhiteSpace(providerMessageId))
//            {
//                var byMsgId = await _db.MessageLogs
//                    .OrderByDescending(x => x.CreatedAt)
//                    .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.ProviderMessageId == providerMessageId);
//                if (byMsgId != null) return byMsgId;
//            }

//            if (!string.IsNullOrWhiteSpace(conversationId))
//            {
//                var byConv = await _db.MessageLogs
//                    .OrderByDescending(x => x.CreatedAt)
//                    .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.ConversationId == conversationId);
//                if (byConv != null) return byConv;
//            }

//            return null;
//        }

//        // Enumerate array property safely
//        private static IEnumerable<JsonElement> Enumerate(JsonElement root, string name)
//        {
//            if (root.ValueKind != JsonValueKind.Object) yield break;
//            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
//            foreach (var x in arr.EnumerateArray()) yield return x;
//        }

//        // Breadth search for any property named `name` (unique name to avoid ambiguity)
//        private static IEnumerable<JsonElement> JsonPathAll(JsonElement root, string name)
//        {
//            if (root.ValueKind == JsonValueKind.Object)
//            {
//                foreach (var p in root.EnumerateObject())
//                {
//                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
//                        yield return p.Value;

//                    foreach (var x in JsonPathAll(p.Value, name))
//                        yield return x;
//                }
//            }
//            else if (root.ValueKind == JsonValueKind.Array)
//            {
//                foreach (var item in root.EnumerateArray())
//                    foreach (var x in JsonPathAll(item, name))
//                        yield return x;
//            }
//        }

//        // Very lightweight "parent" guess: look for an object in ancestry that contains the node reference (best-effort)
//        private static JsonElement? TryGetParentObject(JsonElement root, JsonElement node)
//        {
//            // System.Text.Json doesn't expose parents. We accept best-effort by scanning objects containing 'pricing'
//            if (root.ValueKind == JsonValueKind.Object)
//            {
//                foreach (var p in root.EnumerateObject())
//                {
//                    if (p.Value.ValueKind == JsonValueKind.Object)
//                    {
//                        if (object.ReferenceEquals(p.Value, node)) return root;
//                        var cand = TryGetParentObject(p.Value, node);
//                        if (cand.HasValue) return cand;
//                    }
//                    else if (p.Value.ValueKind == JsonValueKind.Array)
//                    {
//                        foreach (var e in p.Value.EnumerateArray())
//                        {
//                            if (object.ReferenceEquals(e, node)) return root;
//                            var cand = TryGetParentObject(e, node);
//                            if (cand.HasValue) return cand;
//                        }
//                    }
//                }
//            }
//            else if (root.ValueKind == JsonValueKind.Array)
//            {
//                foreach (var e in root.EnumerateArray())
//                {
//                    var cand = TryGetParentObject(e, node);
//                    if (cand.HasValue) return cand;
//                }
//            }
//            return null;
//        }

//        private static string? TryGetString(JsonElement? obj, string name)
//        {
//            if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object) return null;
//            return obj.Value.TryGetProperty(name, out var el) ? el.GetString() : null;
//        }

//        private static string? TryGetNestedString(JsonElement? obj, string name1, string name2)
//        {
//            if (!obj.HasValue || obj.Value.ValueKind != JsonValueKind.Object) return null;
//            if (!obj.Value.TryGetProperty(name1, out var inner) || inner.ValueKind != JsonValueKind.Object) return null;
//            return inner.TryGetProperty(name2, out var v) ? v.GetString() : null;
//        }
//    }
//}
