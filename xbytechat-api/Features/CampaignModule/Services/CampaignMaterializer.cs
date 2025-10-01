﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Serilog;
using xbytechat.api;
using xbytechat.api.Features.CampaignModule.DTOs;
using xbytechat.api.Features.CampaignModule.Helpers;
using xbytechat.api.Features.CampaignModule.Models;

namespace xbytechat.api.Features.CampaignModule.Services
{
    /// <summary>
    /// CSV-based materializer:
    /// - Dry-run: validates + resolves variables/phones (no writes)
    /// - Persist: creates Audience + AudienceMembers + CampaignRecipients with
    ///   ResolvedParametersJson, ResolvedButtonUrlsJson, IdempotencyKey, MaterializedAt.
    /// </summary>
    public sealed class CampaignMaterializer : ICampaignMaterializer
    {
        private readonly AppDbContext _db;
        private readonly IVariableResolver _resolver;

        // Common phone header candidates (case-insensitive)
        private static readonly string[] PhoneHeaderCandidates =
        {
            "phone", "mobile", "whatsapp", "msisdn", "whatsapp_number", "contact", "contact_number"
        };

        public CampaignMaterializer(AppDbContext db, IVariableResolver resolver)
        {
            _db = db;
            _resolver = resolver;
        }
        // === NEW: infer mappings when FE did not send or sent partial mappings =========
        private static Dictionary<string, string> BuildAutoMappingsFromRow(
            IDictionary<string, string> rowDict,
            int requiredBodySlots // 0 if unknown
        )
        {
            // We will map to the variable keys your IVariableResolver expects:
            //  - "{{1}}" -> CSV column name
            //  - "header.text_paramN" -> CSV column
            //  - "buttonN.url_param"  -> CSV column
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1) body: parameter1..N  -> {{1}}..{{N}}
            // If N unknown, infer by scanning parameter\d+ in row headers
            int n = requiredBodySlots > 0
                ? requiredBodySlots
                : rowDict.Keys.Select(k =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(k, @"^parameter(\d+)$", RegexOptions.IgnoreCase);
                    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
                }).DefaultIfEmpty(0).Max();

            for (int i = 1; i <= n; i++)
            {
                var csvHeader = rowDict.Keys.FirstOrDefault(k => string.Equals(k, $"parameter{i}", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(csvHeader))
                    map[$"{{{{{i}}}}}"] = csvHeader; // -> {{i}}
            }

            // 2) header text variables: headerparaN -> header.text_paramN
            foreach (var kv in rowDict)
            {
                var m = System.Text.RegularExpressions.Regex.Match(kv.Key, @"^headerpara(\d+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var slot = int.Parse(m.Groups[1].Value);
                    map[$"header.text_param{slot}"] = kv.Key;
                }
            }

            // 3) dynamic URL buttons: buttonparaN -> buttonN.url_param
            foreach (var kv in rowDict)
            {
                var m = System.Text.RegularExpressions.Regex.Match(kv.Key, @"^buttonpara(\d+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var pos = int.Parse(m.Groups[1].Value);
                    if (pos >= 1 && pos <= 3)
                        map[$"button{pos}.url_param"] = kv.Key;
                }
            }

            return map;
        }

        // === NEW: read the template’s body placeholder count for strict enforcement ====
        private async Task<int> GetRequiredBodySlotsAsync(Guid businessId, Guid campaignId, CancellationToken ct)
        {
            // Try reading campaign -> template name and then WhatsAppTemplates.PlaceholderCount
            var data = await _db.Campaigns
                .AsNoTracking()
                .Where(c => c.Id == campaignId && c.BusinessId == businessId)
                .Select(c => new { c.MessageTemplate, c.TemplateId })
                .FirstOrDefaultAsync(ct);

            var templateName = !string.IsNullOrWhiteSpace(data?.TemplateId)
                ? data!.TemplateId!
                : (data?.MessageTemplate ?? string.Empty);

            if (string.IsNullOrWhiteSpace(templateName))
                return 0;

            // Use the most recent active row
            var tpl = await _db.WhatsAppTemplates
                .AsNoTracking()
                .Where(t => t.BusinessId == businessId && t.IsActive && t.Name == templateName)
                .OrderByDescending(t => t.UpdatedAt > t.CreatedAt ? t.UpdatedAt : t.CreatedAt)
                .FirstOrDefaultAsync(ct);

            return tpl?.PlaceholderCount ?? 0;
        }

        // === NEW: ensure body params are complete; return null when missing ============
        private static string[]? EnsureBodyParamsComplete(string[] bodyParams, int requiredSlots, out List<string> missing)
        {
            missing = new List<string>();
            if (requiredSlots <= 0) return bodyParams; // nothing to enforce

            // Resize to requiredSlots
            var arr = new string[requiredSlots];
            for (int i = 0; i < requiredSlots; i++)
            {
                var v = (i < bodyParams.Length ? bodyParams[i] : string.Empty) ?? string.Empty;
                arr[i] = v;
                if (string.IsNullOrWhiteSpace(v))
                    missing.Add($"{{{{{i + 1}}}}}");
            }

            if (missing.Count > 0)
                return null;

            return arr;
        }

        // File: Features/CampaignModule/Services/CampaignMaterializer.cs
        // Method: CreateAsync(...)
        // NOTE: This version is identical to yours except we pull `requiredBodySlots` ONCE before the foreach.
        //       Everything else remains the same (including the enforcement you added).

        public async Task<CampaignCsvMaterializeResponseDto> CreateAsync(
            Guid businessId,
            Guid campaignId,
            CampaignCsvMaterializeRequestDto request,
            CancellationToken ct = default)
        {
            if (businessId == Guid.Empty) throw new UnauthorizedAccessException("Invalid business id.");
            if (campaignId == Guid.Empty) throw new ArgumentException("Invalid campaign id.");
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (request.CsvBatchId == Guid.Empty) throw new ArgumentException("CsvBatchId is required.");
            if (request.Persist && string.IsNullOrWhiteSpace(request.AudienceName))
                throw new ArgumentException("AudienceName is required when Persist=true.");

            // Campaign ownership check
            var owns = await _db.Campaigns
                .AsNoTracking()
                .AnyAsync(c => c.Id == campaignId && c.BusinessId == businessId, ct);
            if (!owns) throw new UnauthorizedAccessException("Campaign not found or not owned by this business.");

            // Load CSV rows for the batch
            var rowsQuery = _db.CsvRows
                .AsNoTracking()
                .Where(r => r.BusinessId == businessId && r.BatchId == request.CsvBatchId)
                .OrderBy(r => r.RowIndex);

            var totalRows = await rowsQuery.CountAsync(ct);
            var csvRows = (request.Limit.HasValue && request.Limit.Value > 0)
                ? await rowsQuery.Take(request.Limit.Value).ToListAsync(ct)
                : await rowsQuery.ToListAsync(ct);

            var resp = new CampaignCsvMaterializeResponseDto
            {
                CampaignId = campaignId,
                CsvBatchId = request.CsvBatchId,
                TotalRows = totalRows
            };

            // Build header set to help autodetect phone field
            var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in csvRows)
            {
                foreach (var k in JsonToDict(r.DataJson).Keys)
                    headerSet.Add(k);
            }

            // Mapping precedence: request → fallback header==token
            var effectiveMappings = request.Mappings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Determine phone field
            var phoneField = request.PhoneField;
            if (string.IsNullOrWhiteSpace(phoneField))
                phoneField = PhoneHeaderCandidates.FirstOrDefault(headerSet.Contains);

            if (string.IsNullOrWhiteSpace(phoneField))
                resp.Warnings.Add("No phone field provided or detected; rows without phone will be skipped.");

            // 🔎 Pull required body slots ONCE (avoid N queries)
            var requiredBodySlots = await GetRequiredBodySlotsAsync(businessId, campaignId, ct);

            var seenPhones = new HashSet<string>(StringComparer.Ordinal);
            var preview = resp.Preview; // alias

            foreach (var row in csvRows)
            {
                ct.ThrowIfCancellationRequested();

                var rowDict = JsonToDict(row.DataJson);
                var m = new CsvMaterializedRowDto { RowIndex = row.RowIndex };

                // 🧭 effective mappings: request.Mappings OR auto-infer from row
                var mappingsToUse =
                    (effectiveMappings != null && effectiveMappings.Count > 0)
                        ? new Dictionary<string, string>(effectiveMappings, StringComparer.OrdinalIgnoreCase)
                        : BuildAutoMappingsFromRow(rowDict, requiredBodySlots);

                // Variables for template (canonicalized by resolver)
                m.Variables = _resolver.ResolveVariables(rowDict, mappingsToUse);

                // Phone selection
                string? phone = null;
                if (!string.IsNullOrWhiteSpace(phoneField))
                {
                    rowDict.TryGetValue(phoneField, out phone);
                }
                else
                {
                    foreach (var cand in PhoneHeaderCandidates)
                        if (rowDict.TryGetValue(cand, out phone) && !string.IsNullOrWhiteSpace(phone))
                            break;
                }

                phone = NormalizePhoneMaybe(phone, request.NormalizePhones);
                m.Phone = phone;

                if (string.IsNullOrWhiteSpace(m.Phone))
                {
                    m.Errors.Add("Missing phone");
                    resp.SkippedCount++;
                    continue;
                }

                if (request.Deduplicate && !seenPhones.Add(m.Phone))
                {
                    m.Errors.Add("Duplicate phone (deduped)");
                    resp.SkippedCount++;
                    continue;
                }

                // 🔒 Enforce required body placeholders BEFORE adding to preview
                var prelimBodyParams = BuildBodyParamArrayFromVariables(
                    m.Variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

                var enforced = EnsureBodyParamsComplete(prelimBodyParams, requiredBodySlots, out var missingSlots);
                if (enforced == null)
                {
                    // at least one required slot missing → skip this row
                    m.Errors.Add($"Missing body parameters: {string.Join(", ", missingSlots)}");
                    resp.SkippedCount++;
                    continue;
                }

                // (Optional) keep for troubleshooting:
                // m.DebugBodyParams = enforced;

                preview.Add(m);
            }

            resp.MaterializedCount = preview.Count;

            // Persist if requested
            if (request.Persist && resp.MaterializedCount > 0)
            {
                var audienceId = await PersistAudienceAndRecipientsAsync(
                    businessId, campaignId, request.AudienceName!, preview, ct);

                resp.AudienceId = audienceId;
            }

            return resp;
        }



        // ---------- Persistence ----------
        // NEW: reusable helper (body {{n}} → string[])
        // NEW: reusable helper (body {{n}} / parameterN / body.N → string[])
        private static string[] BuildBodyParamArrayFromVariables(IDictionary<string, string> vars)
        {
            var pairs = new List<(int idx, string val)>();

            foreach (var kv in vars)
            {
                var k = kv.Key ?? string.Empty;
                var v = kv.Value ?? string.Empty;

                // 1) body.N
                if (k.StartsWith("body.", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(k.AsSpan("body.".Length), out var n) && n > 0)
                        pairs.Add((n, v));
                    continue;
                }

                // 2) parameterN (FE mapping keys)
                if (k.StartsWith("parameter", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(k.AsSpan("parameter".Length), out var n) && n > 0)
                        pairs.Add((n, v));
                    continue;
                }

                // 3) {{N}} (auto-mapper tokens)
                // match exactly {{  number  }}
                var m = System.Text.RegularExpressions.Regex.Match(k, @"^\{\{\s*(\d+)\s*\}\}$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var t) && t > 0)
                {
                    pairs.Add((t, v));
                    continue;
                }
            }

            if (pairs.Count == 0) return Array.Empty<string>();

            var max = pairs.Max(p => p.idx);
            var arr = new string[max];
            for (int i = 0; i < max; i++) arr[i] = string.Empty;
            foreach (var (idx, val) in pairs) arr[idx - 1] = val ?? string.Empty;
            return arr;
        }

        // CampaignMaterializer.cs  — replace the whole method
        private async Task<Guid> PersistAudienceAndRecipientsAsync(
            Guid businessId,
            Guid campaignId,
            string audienceName,
            List<CsvMaterializedRowDto> rows,
            CancellationToken ct)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var now = DateTime.UtcNow;

                var audience = new Audience
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    Name = audienceName,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.Audiences.Add(audience);

                // --- local helpers --------------------------------------------------
                static string[] BuildBodyParamArray(IDictionary<string, string> vars)
                {
                    // Accept both "body.N" and "parameterN"
                    var pairs = new List<(int idx, string val)>();

                    foreach (var kv in vars)
                    {
                        var k = kv.Key;

                        // body.N
                        if (k.StartsWith("body.", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(k.AsSpan("body.".Length), out var n) && n > 0)
                                pairs.Add((n, kv.Value ?? string.Empty));
                            continue;
                        }

                        // parameterN (compat)
                        if (k.StartsWith("parameter", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(k.AsSpan("parameter".Length), out var n) && n > 0)
                                pairs.Add((n, kv.Value ?? string.Empty));
                        }
                    }

                    if (pairs.Count == 0) return Array.Empty<string>();

                    var max = pairs.Max(p => p.idx);
                    var arr = new string[max];
                    for (int i = 0; i < max; i++) arr[i] = string.Empty;
                    foreach (var (idx, val) in pairs) arr[idx - 1] = val ?? string.Empty;
                    return arr;
                }

                static Dictionary<string, string> BuildHeaderAndButtonVars(IDictionary<string, string> vars)
                {
                    // We store non-body keys in ResolvedButtonUrlsJson (generic bag):
                    // - header.image_url / header.video_url / header.document_url
                    // - header.text.N
                    // - button{1..3}.url_param
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in vars)
                    {
                        var k = kv.Key;
                        var v = kv.Value ?? string.Empty;

                        // header media urls
                        if (k.StartsWith("header.", StringComparison.OrdinalIgnoreCase) &&
                           (k.EndsWith("_url", StringComparison.OrdinalIgnoreCase) ||
                            k.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
                        {
                            dict[k] = v;
                            continue;
                        }

                        // header text placeholders: header.text.N
                        if (k.StartsWith("header.text.", StringComparison.OrdinalIgnoreCase))
                        {
                            var tail = k.Substring("header.text.".Length);
                            if (int.TryParse(tail, out var n) && n > 0)
                                dict[k] = v;
                            continue;
                        }

                        // URL button param variants → normalize to .url_param
                        if (k.StartsWith("button", StringComparison.OrdinalIgnoreCase))
                        {
                            var normKey = k
                                .Replace(".url.param", ".url_param", StringComparison.OrdinalIgnoreCase)
                                .Replace(".urlparam", ".url_param", StringComparison.OrdinalIgnoreCase);

                            if (normKey.EndsWith(".url_param", StringComparison.OrdinalIgnoreCase))
                                dict[normKey] = v;
                        }
                    }

                    return dict;
                }
                // --------------------------------------------------------------------

                foreach (var r in rows)
                {
                    if (string.IsNullOrWhiteSpace(r.Phone))
                        continue; // safety; missing phone rows were already filtered

                    // Try to link to an existing Contact by normalized phone
                    Guid? contactId = await _db.Contacts
                        .Where(c => c.BusinessId == businessId && c.PhoneNumber == r.Phone)
                        .Select(c => (Guid?)c.Id)
                        .FirstOrDefaultAsync(ct);

                    var variables = r.Variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    // Keep full variable map on AudienceMember for export/debug
                    var attributesJson = JsonSerializer.Serialize(variables);

                    // Shapes expected by sender:
                    var bodyParams = BuildBodyParamArray(variables);            // string[] for {{1}}..{{N}}
                    var headerAndButtons = BuildHeaderAndButtonVars(variables); // dict for header.* + button*.url_param

                    var resolvedParamsJson = JsonSerializer.Serialize(bodyParams);
                    var resolvedButtonsJson = JsonSerializer.Serialize(headerAndButtons);

                    // Idempotency: include both body params and header/button vars
                    var idemPayload = JsonSerializer.Serialize(new { p = bodyParams, b = headerAndButtons });
                    var idempotencyKey = ComputeIdempotencyKey(campaignId, r.Phone, idemPayload);

                    var member = new AudienceMember
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        AudienceId = audience.Id,
                        ContactId = contactId,                 // stays null if no match
                        PhoneE164 = r.Phone,                   // normalized earlier
                        AttributesJson = attributesJson,
                        IsTransientContact = !contactId.HasValue,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    _db.AudiencesMembers.Add(member);

                    var recipient = new CampaignRecipient
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        CampaignId = campaignId,
                        AudienceMemberId = member.Id,
                        IdempotencyKey = idempotencyKey,
                        ResolvedParametersJson = resolvedParamsJson,   // string[] (body)
                        ResolvedButtonUrlsJson = resolvedButtonsJson,  // dict (header + buttons)
                        MaterializedAt = now,
                        Status = "Pending",
                        UpdatedAt = now
                    };

                    if (contactId.HasValue)
                        recipient.ContactId = contactId.Value;

                    _db.CampaignRecipients.Add(recipient);
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return audience.Id;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Materialize persist failed");
                await tx.RollbackAsync(ct);
                throw;
            }
        }


        // ---------- Utils ----------
        private static Dictionary<string, string> JsonToDict(string? json)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return dict;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
            foreach (var p in doc.RootElement.EnumerateObject())
                dict[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? "" : p.Value.ToString();

            return dict;
        }

        private static string? NormalizePhoneMaybe(string? raw, bool normalize)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw.Trim();
            if (!normalize) return trimmed;

            // naive E.164-lite cleanup; swap for your real normalizer later
            var digits = Regex.Replace(trimmed, "[^0-9]", "");
            digits = digits.TrimStart('0');

            // Heuristic for India: add 91 if 10-digit local
            if (digits.Length == 10) digits = "91" + digits;

            return digits.Length >= 10 ? digits : trimmed;
        }

        private static string ComputeIdempotencyKey(Guid campaignId, string phone, string parametersJson)
        {
            var raw = $"{campaignId}|{phone}|{parametersJson}";
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }
    }
}



