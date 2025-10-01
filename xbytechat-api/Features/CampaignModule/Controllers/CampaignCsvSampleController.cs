using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using xbytechat.api;
using xbytechat.api.AuthModule.Models;
using xbytechat.api.Features.CampaignModule.Services;
using xbytechat.api.Shared;
using xbytechat_api.WhatsAppSettings.Services; // User.GetBusinessId()

namespace xbytechat.api.Features.CampaignModule.Controllers
{
    [ApiController]
    [Route("api/campaigns/{campaignId:guid}/csv-sample")]
    [Authorize]
    public sealed class CampaignCsvSampleController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ICampaignPreviewService _preview;
        private readonly IWhatsAppTemplateFetcherService _tpl;

        public CampaignCsvSampleController(AppDbContext db, ICampaignPreviewService preview, IWhatsAppTemplateFetcherService tpl)
        {
            _db = db;
            _preview = preview;
            _tpl = tpl;
        }

        private sealed class SchemaResult
        {
            public bool Found { get; set; }
            public List<string> Headers { get; set; } = new(); // dynamic per-row CSV columns (no phone, no media url)
            public int PlaceholderCount { get; set; }           // body placeholders count
            public string HeaderType { get; set; } = "none";     // "none" | "image" | "video" | "document" | "text"
            public bool HeaderNeedsUrl { get; set; }             // true for image/video/document
        }

        // GET /api/campaigns/{campaignId}/csv-sample/schema
        // GET /api/campaigns/{campaignId}/csv-sample/schema
        [HttpGet("schema")]
        public async Task<IActionResult> GetSchema([FromRoute] Guid campaignId, CancellationToken ct = default)
        {
            var businessId = User.GetBusinessId();
            if (businessId == Guid.Empty) return Unauthorized();

            var schema = await BuildSchemaAsync(businessId, campaignId, ct);
            if (!schema.Found) return NotFound();

            // Flatten so FE can read sc.headers / sc.placeholderCount
            return Ok(new
            {
                headers = schema.Headers,                   // e.g. ["parameter1","headerpara1","buttonpara1"]
                placeholderCount = schema.PlaceholderCount, // body placeholders count
                header = new
                {
                    type = schema.HeaderType,               // "none" | "image" | "video" | "document" | "text"
                    needsUrl = schema.HeaderNeedsUrl        // true for image/video/document
                }
            });
        }

        // GET /api/campaigns/{campaignId}/csv-sample
        // -> returns ONLY the header row (no sample values)
        [HttpGet]
        public async Task<IActionResult> Download([FromRoute] Guid campaignId, CancellationToken ct = default)
        {
            var businessId = User.GetBusinessId();
            if (businessId == Guid.Empty) return Unauthorized();

            var schema = await BuildSchemaAsync(businessId, campaignId, ct);
            if (!schema.Found) return NotFound();

            // phone is always first; then our dynamic columns (already lowercased)
            var headers = new List<string> { "phone" };
            headers.AddRange(schema.Headers);

            var line = string.Join(",", headers.Select(EscapeCsv));
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            var fileName = $"campaign-{campaignId:N}-sample.csv";
            return File(bytes, "text/csv; charset=utf-8", fileName);
        }

        // -------- schema builder (creates lowercase column names) --------
        private async Task<SchemaResult> BuildSchemaAsync(Guid businessId, Guid campaignId, CancellationToken ct)
        {
            var campaign = await _db.Campaigns
                .AsNoTracking()
                .Where(c => c.Id == campaignId && c.BusinessId == businessId && !c.IsDeleted)
                .Select(c => new { c.Id, c.BusinessId, c.TemplateId, c.MessageTemplate, c.Provider })
                .FirstOrDefaultAsync(ct);

            if (campaign == null)
                return new SchemaResult { Found = false };

            var templateName =
                !string.IsNullOrWhiteSpace(campaign.TemplateId) ? campaign.TemplateId! :
                !string.IsNullOrWhiteSpace(campaign.MessageTemplate) ? campaign.MessageTemplate! :
                string.Empty;

            if (string.IsNullOrWhiteSpace(templateName))
            {
                return new SchemaResult
                {
                    Found = true,
                    Headers = new List<string>(), // dynamic columns only; "phone" is added by the downloader
                    PlaceholderCount = 0,
                    HeaderType = "none",
                    HeaderNeedsUrl = false
                };
            }

            var provider = (campaign.Provider ?? "META_CLOUD").ToUpperInvariant();

            // 1) Normalized meta from service
            var meta = await _tpl.GetTemplateMetaAsync(
                campaign.BusinessId,
                templateName,
                language: null,
                provider: provider
            );

            // 2) DB row fallback — NOTE: provider filter REMOVED to avoid mismatches
            var tplRow = await _db.WhatsAppTemplates
                .AsNoTracking()
                .Where(w => w.BusinessId == businessId
                            && w.IsActive
                            && w.Name == templateName)
                .OrderByDescending(w => w.UpdatedAt > w.CreatedAt ? w.UpdatedAt : w.CreatedAt)
                .FirstOrDefaultAsync(ct);

            var headers = new List<string>();

            // BODY placeholders -> parameter1..N
            int bodyCount = 0;
            if (meta?.BodyPlaceholders is { Count: > 0 })
            {
                bodyCount = meta.BodyPlaceholders.Count;
            }
            else if (tplRow?.PlaceholderCount is int pc && pc > 0)
            {
                bodyCount = pc;
            }
            else
            {
                var bodyText = meta?.GetType().GetProperty("Body")?.GetValue(meta) as string;
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    var m = Regex.Matches(bodyText, @"\{\{\s*(\d+)\s*\}\}");
                    if (m.Count > 0)
                        bodyCount = m.Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();
                }
            }
            for (int i = 1; i <= bodyCount; i++) headers.Add($"parameter{i}");

            // HEADER detection (media is campaign-level; text header may have params)
            string headerTypeNorm = (meta?.HeaderType ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(headerTypeNorm) && tplRow?.HasImageHeader == true)
                headerTypeNorm = "IMAGE";

            string respHeaderType = "none";
            bool needsUrl = false;
            switch (headerTypeNorm)
            {
                case "IMAGE": respHeaderType = "image"; needsUrl = true; break;
                case "VIDEO": respHeaderType = "video"; needsUrl = true; break;
                case "DOCUMENT":
                case "PDF": respHeaderType = "document"; needsUrl = true; break;
                case "TEXT": respHeaderType = "text"; needsUrl = false; break;
                default: respHeaderType = "none"; needsUrl = false; break;
            }

            // Text header placeholders -> headerpara1..M
            if (respHeaderType == "text")
            {
                int headerVarCount = 0;
                var hpProp = meta?.GetType().GetProperty("HeaderPlaceholders");
                if (hpProp?.GetValue(meta) is IEnumerable<object> hpEnum)
                {
                    headerVarCount = hpEnum.Cast<object>().Count();
                }
                else
                {
                    var headerText =
                        meta?.GetType().GetProperty("Header")?.GetValue(meta) as string ??
                        meta?.GetType().GetProperty("HeaderText")?.GetValue(meta) as string ?? "";
                    if (!string.IsNullOrWhiteSpace(headerText))
                    {
                        var m = Regex.Matches(headerText, @"\{\{\s*(\d+)\s*\}\}");
                        if (m.Count > 0)
                            headerVarCount = m.Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();
                    }
                }
                for (int i = 1; i <= headerVarCount; i++)
                    headers.Add($"headerpara{i}");
            }

            // Dynamic URL buttons -> buttonpara1..3
            bool LooksDynamic(string? val) => !string.IsNullOrEmpty(val) && val.Contains("{{");
            bool IsUrlish(string? type, string? subType)
            {
                type = (type ?? "").ToLowerInvariant();
                subType = (subType ?? "").ToLowerInvariant();
                return type == "url" || subType == "url";
            }
            int GetPos(object b, int fallbackOneBased)
            {
                var t = b.GetType();
                if (t.GetProperty("Index")?.GetValue(b) is int idx && idx > 0) return idx;
                if (t.GetProperty("Order")?.GetValue(b) is int ord && ord >= 0) return ord + 1;
                if (t.GetProperty("Position")?.GetValue(b) is int pos && pos > 0) return pos;
                return fallbackOneBased;
            }

            var positions = new SortedSet<int>();

            // A) normalized meta.Buttons
            if (meta?.Buttons is { Count: > 0 })
            {
                var urlBtns = meta.Buttons
                    .Where(b => IsUrlish(b.GetType().GetProperty("Type")?.GetValue(b) as string,
                                         b.GetType().GetProperty("SubType")?.GetValue(b) as string))
                    .ToList();

                for (int i = 0; i < urlBtns.Count && positions.Count < 3; i++)
                {
                    var b = urlBtns[i];
                    var val = b.GetType().GetProperty("Value")?.GetValue(b) as string;
                    var subType = b.GetType().GetProperty("SubType")?.GetValue(b) as string;
                    var urlType = b.GetType().GetProperty("UrlType")?.GetValue(b) as string;
                    var hasPh = b.GetType().GetProperty("HasPlaceholder")?.GetValue(b) as bool?;

                    var dynamicByProps =
                        LooksDynamic(val) ||
                        string.Equals(subType, "DYNAMIC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(urlType, "DYNAMIC", StringComparison.OrdinalIgnoreCase) ||
                        hasPh == true;

                    if (dynamicByProps)
                    {
                        var pos = GetPos(b, i + 1);
                        if (pos >= 1 && pos <= 3) positions.Add(pos);
                    }
                }
            }

            // B) ButtonsJson fallback
            if (positions.Count < 3 && !string.IsNullOrWhiteSpace(tplRow?.ButtonsJson))
            {
                try
                {
                    var root = JsonDocument.Parse(tplRow.ButtonsJson).RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in root.EnumerateArray())
                        {
                            if (positions.Count >= 3) break;

                            var type = el.TryGetProperty("type", out var tkn) ? tkn.GetString() : null;
                            if (!IsUrlish(type, null)) continue;

                            int pos = 0;
                            if (el.TryGetProperty("index", out var iTok) && iTok.TryGetInt32(out var idx) && idx > 0) pos = idx;
                            else if (el.TryGetProperty("order", out var oTok) && oTok.TryGetInt32(out var ord) && ord >= 0) pos = ord + 1;

                            string? value = null;
                            if (el.TryGetProperty("value", out var vTok) && vTok.ValueKind == JsonValueKind.String)
                                value = vTok.GetString();

                            bool hasParam = false;
                            if (el.TryGetProperty("parameters", out var pTok) && pTok.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var p in pTok.EnumerateArray())
                                {
                                    if (p.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                                    {
                                        var s = txt.GetString();
                                        if (!string.IsNullOrEmpty(s) && s.Contains("{{")) { hasParam = true; break; }
                                    }
                                }
                            }

                            if (LooksDynamic(value) || hasParam)
                            {
                                if (pos <= 0) pos = positions.Count + 1;
                                if (pos >= 1 && pos <= 3) positions.Add(pos);
                            }
                        }
                    }
                }
                catch { /* ignore bad JSON */ }
            }

            foreach (var pos in positions)
            {
                var key = $"buttonpara{pos}";
                if (!headers.Contains(key, StringComparer.OrdinalIgnoreCase))
                    headers.Add(key);
            }

            // ensure lowercase
            headers = headers.Select(h => h.ToLowerInvariant()).ToList();

            return new SchemaResult
            {
                Found = true,
                Headers = headers,
                PlaceholderCount = bodyCount,
                HeaderType = respHeaderType,
                HeaderNeedsUrl = needsUrl
            };
        }

        private static string EscapeCsv(string input)
        {
            if (input == null) return "";
            var needsQuotes = input.Contains(',') || input.Contains('"') || input.Contains('\n') || input.Contains('\r');
            var s = input.Replace("\"", "\"\"");
            return needsQuotes ? $"\"{s}\"" : s;
        }
    }
}

//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Text.Json;
//using System.Text.RegularExpressions;
//using System.Threading;
//using System.Threading.Tasks;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using xbytechat.api;
//using xbytechat.api.AuthModule.Models;
//using xbytechat.api.Features.CampaignModule.Services;
//using xbytechat.api.Shared;
//using xbytechat_api.WhatsAppSettings.Services; // User.GetBusinessId()

//namespace xbytechat.api.Features.CampaignModule.Controllers
//{
//    [ApiController]
//    [Route("api/campaigns/{campaignId:guid}/csv-sample")]
//    [Authorize]
//    public sealed class CampaignCsvSampleController : ControllerBase
//    {
//        private readonly AppDbContext _db;
//        private readonly ICampaignPreviewService _preview;
//        private readonly IWhatsAppTemplateFetcherService _tpl;
//        public CampaignCsvSampleController(AppDbContext db, ICampaignPreviewService preview, IWhatsAppTemplateFetcherService tpl)
//        {
//            _db = db;
//            _preview = preview;
//            _tpl = tpl;
//        }

//        // Add at top of file if missing:


//        // -----------------------------------------------
//        // DTO used internally for building the schema
//        // -----------------------------------------------
//        private sealed class SchemaResult
//        {
//            public bool Found { get; set; }
//            public List<string> Headers { get; set; } = new();
//            public int PlaceholderCount { get; set; } // body placeholders count
//            public string HeaderType { get; set; } = "none"; // "none" | "image" | "video" | "document" | "text"
//            public bool HeaderNeedsUrl { get; set; } // true for image/video/document header
//        }

//        // -----------------------------------------------
//        // GET /campaigns/{campaignId}/csv-sample/schema
//        // -----------------------------------------------
//        [HttpGet("schema")]
//        public async Task<IActionResult> GetSchema([FromRoute] Guid campaignId, CancellationToken ct = default)
//        {
//            var businessId = User.GetBusinessId();
//            if (businessId == Guid.Empty) return Unauthorized();

//            var schema = await BuildSchemaAsync(businessId, campaignId, ct);
//            if (!schema.Found) return NotFound();

//            return Ok(new
//            {
//                success = true,
//                data = new
//                {
//                    headers = schema.Headers,                     // ONLY per-row CSV fields (no phone, no media URL)
//                    placeholderCount = schema.PlaceholderCount,   // body placeholders count
//                    header = new
//                    {
//                        type = schema.HeaderType,                 // "none" | "image" | "video" | "document" | "text"
//                        needsUrl = schema.HeaderNeedsUrl          // true iff header is image/video/document
//                    }
//                }
//            });
//        }

//        // -----------------------------------------------
//        // Builder
//        // -----------------------------------------------
//        private async Task<SchemaResult> BuildSchemaAsync(Guid businessId, Guid campaignId, CancellationToken ct)
//        {
//            var campaign = await _db.Campaigns
//                .AsNoTracking()
//                .Where(c => c.Id == campaignId && c.BusinessId == businessId && !c.IsDeleted)
//                .Select(c => new { c.Id, c.BusinessId, c.TemplateId, c.MessageTemplate, c.Provider })
//                .FirstOrDefaultAsync(ct);

//            if (campaign == null)
//                return new SchemaResult { Found = false };

//            var templateName =
//                !string.IsNullOrWhiteSpace(campaign.TemplateId) ? campaign.TemplateId! :
//                !string.IsNullOrWhiteSpace(campaign.MessageTemplate) ? campaign.MessageTemplate! :
//                string.Empty;

//            // If no template selected, we still return a valid, empty schema (FE will just ask for phone).
//            if (string.IsNullOrWhiteSpace(templateName))
//            {
//                return new SchemaResult
//                {
//                    Found = true,
//                    Headers = new List<string>(),     // do NOT inject "phone" here
//                    PlaceholderCount = 0,
//                    HeaderType = "none",
//                    HeaderNeedsUrl = false
//                };
//            }

//            var provider = (campaign.Provider ?? "META_CLOUD").ToUpperInvariant();

//            // 1) Normalized meta from your template service
//            var meta = await _tpl.GetTemplateMetaAsync(
//                campaign.BusinessId,
//                templateName,
//                language: null,
//                provider: provider
//            );

//            // 2) Fallback to raw WhatsAppTemplates row
//            var tplRow = await _db.WhatsAppTemplates
//                .AsNoTracking()
//                .Where(w => w.BusinessId == businessId
//                         && w.IsActive
//                         && w.Name == templateName
//                         && w.Provider == provider)
//                .OrderByDescending(w => w.UpdatedAt > w.CreatedAt ? w.UpdatedAt : w.CreatedAt)
//                .FirstOrDefaultAsync(ct);

//            var headers = new List<string>(); // <- per-row CSV columns only (NO "phone", NO media URL constant)

//            // ---------------- BODY placeholders ----------------
//            int bodyCount = 0;

//            if (meta?.BodyPlaceholders is { Count: > 0 })
//            {
//                bodyCount = meta.BodyPlaceholders.Count;
//            }
//            else if (tplRow?.PlaceholderCount is int pc && pc > 0)
//            {
//                bodyCount = pc;
//            }
//            else
//            {
//                // Last resort: parse {{n}} from a 'Body' text if present
//                var bodyText = meta?.GetType().GetProperty("Body")?.GetValue(meta) as string;
//                if (!string.IsNullOrWhiteSpace(bodyText))
//                {
//                    var m = Regex.Matches(bodyText, @"\{\{\s*(\d+)\s*\}\}");
//                    if (m.Count > 0)
//                        bodyCount = m.Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();
//                }
//            }
//            for (int i = 1; i <= bodyCount; i++) headers.Add($"body.{i}");

//            // ---------------- HEADER type + header text placeholders ----------------
//            // Normalize header type
//            string headerTypeNorm = (meta?.HeaderType ?? "").Trim().ToUpperInvariant();
//            if (string.IsNullOrEmpty(headerTypeNorm) && tplRow?.HasImageHeader == true)
//                headerTypeNorm = "IMAGE"; // legacy fallback

//            // Map to response type + needsUrl
//            string respHeaderType = "none";
//            bool needsUrl = false;

//            switch (headerTypeNorm)
//            {
//                case "IMAGE":
//                    respHeaderType = "image";
//                    needsUrl = true;
//                    break;
//                case "VIDEO":
//                    respHeaderType = "video";
//                    needsUrl = true;
//                    break;
//                case "DOCUMENT":
//                case "PDF":
//                    respHeaderType = "document";
//                    needsUrl = true;
//                    break;
//                case "TEXT":
//                    respHeaderType = "text";
//                    needsUrl = false;
//                    break;
//                default:
//                    respHeaderType = "none";
//                    needsUrl = false;
//                    break;
//            }

//            // If header is TEXT, include its own placeholders as header.1, header.2, ...
//            if (respHeaderType == "text")
//            {
//                int headerVarCount = 0;

//                // Prefer an explicit placeholder list if your meta has it
//                var headerPlaceholdersProp = meta?.GetType().GetProperty("HeaderPlaceholders");
//                if (headerPlaceholdersProp?.GetValue(meta) is IEnumerable<object> hpEnum)
//                {
//                    headerVarCount = hpEnum.Cast<object>().Count();
//                }
//                else
//                {
//                    // Fallback: parse {{n}} from header text
//                    var headerText =
//                        meta?.GetType().GetProperty("Header")?.GetValue(meta) as string ??
//                        meta?.GetType().GetProperty("HeaderText")?.GetValue(meta) as string ??
//                        string.Empty;

//                    if (!string.IsNullOrWhiteSpace(headerText))
//                    {
//                        var m = Regex.Matches(headerText, @"\{\{\s*(\d+)\s*\}\}");
//                        if (m.Count > 0)
//                            headerVarCount = m.Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();
//                    }
//                }

//                for (int i = 1; i <= headerVarCount; i++)
//                    headers.Add($"header.{i}");
//            }

//            // ---------------- DYNAMIC URL BUTTONS -> button{i}.url_param ----------------
//            bool LooksDynamic(string? val) => !string.IsNullOrEmpty(val) && val.Contains("{{");

//            bool IsUrlish(string? type, string? subType)
//            {
//                type = (type ?? "").ToLowerInvariant();
//                subType = (subType ?? "").ToLowerInvariant();
//                return type == "url" || subType == "url";
//            }

//            int GetPos(object b, int fallbackOneBased)
//            {
//                var t = b.GetType();
//                if (t.GetProperty("Index")?.GetValue(b) is int idx && idx > 0) return idx;
//                if (t.GetProperty("Order")?.GetValue(b) is int ord && ord >= 0) return ord + 1;
//                if (t.GetProperty("Position")?.GetValue(b) is int pos && pos > 0) return pos;
//                return fallbackOneBased;
//            }

//            var positions = new SortedSet<int>();

//            // A) meta.Buttons (normalized)
//            if (meta?.Buttons is { Count: > 0 })
//            {
//                var urlBtns = meta.Buttons
//                    .Where(b => IsUrlish(b.GetType().GetProperty("Type")?.GetValue(b) as string,
//                                         b.GetType().GetProperty("SubType")?.GetValue(b) as string))
//                    .ToList();

//                for (int i = 0; i < urlBtns.Count && positions.Count < 3; i++)
//                {
//                    var b = urlBtns[i];
//                    var val = b.GetType().GetProperty("Value")?.GetValue(b) as string;
//                    var subType = b.GetType().GetProperty("SubType")?.GetValue(b) as string;
//                    var urlType = b.GetType().GetProperty("UrlType")?.GetValue(b) as string;
//                    var hasPh = b.GetType().GetProperty("HasPlaceholder")?.GetValue(b) as bool?;

//                    var dynamicByProps =
//                        LooksDynamic(val) ||
//                        string.Equals(subType, "DYNAMIC", StringComparison.OrdinalIgnoreCase) ||
//                        string.Equals(urlType, "DYNAMIC", StringComparison.OrdinalIgnoreCase) ||
//                        hasPh == true;

//                    if (dynamicByProps)
//                    {
//                        var pos = GetPos(b, i + 1);
//                        if (pos >= 1 && pos <= 3) positions.Add(pos);
//                    }
//                }
//            }

//            // B) raw ButtonsJson fallback (from DB row)
//            if (positions.Count < 3 && !string.IsNullOrWhiteSpace(tplRow?.ButtonsJson))
//            {
//                try
//                {
//                    var root = System.Text.Json.JsonDocument.Parse(tplRow.ButtonsJson).RootElement;
//                    if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
//                    {
//                        foreach (var el in root.EnumerateArray())
//                        {
//                            if (positions.Count >= 3) break;

//                            var type = el.TryGetProperty("type", out var tkn) ? tkn.GetString() : null;
//                            if (!IsUrlish(type, null)) continue;

//                            int pos = 0;
//                            if (el.TryGetProperty("index", out var iTok) && iTok.TryGetInt32(out var idx) && idx > 0) pos = idx;
//                            else if (el.TryGetProperty("order", out var oTok) && oTok.TryGetInt32(out var ord) && ord >= 0) pos = ord + 1;

//                            string? value = null;
//                            if (el.TryGetProperty("value", out var vTok) && vTok.ValueKind == System.Text.Json.JsonValueKind.String)
//                                value = vTok.GetString();

//                            bool hasParam = false;
//                            if (el.TryGetProperty("parameters", out var pTok) && pTok.ValueKind == System.Text.Json.JsonValueKind.Array)
//                            {
//                                foreach (var p in pTok.EnumerateArray())
//                                {
//                                    if (p.TryGetProperty("text", out var txt) && txt.ValueKind == System.Text.Json.JsonValueKind.String)
//                                    {
//                                        var s = txt.GetString();
//                                        if (!string.IsNullOrEmpty(s) && s.Contains("{{")) { hasParam = true; break; }
//                                    }
//                                }
//                            }

//                            if (LooksDynamic(value) || hasParam)
//                            {
//                                if (pos <= 0) pos = positions.Count + 1;
//                                if (pos >= 1 && pos <= 3) positions.Add(pos);
//                            }
//                        }
//                    }
//                }
//                catch { /* ignore bad JSON */ }
//            }

//            foreach (var pos in positions)
//            {
//                var key = $"button{pos}.url_param";
//                if (!headers.Contains(key, StringComparer.OrdinalIgnoreCase))
//                    headers.Add(key);
//            }

//            // DONE
//            return new SchemaResult
//            {
//                Found = true,
//                Headers = headers,
//                PlaceholderCount = bodyCount,
//                HeaderType = respHeaderType,
//                HeaderNeedsUrl = needsUrl
//            };
//        }





//        [HttpGet]
//        // [HttpGet("download-sample/{campaignId:guid}")]
//        public async Task<IActionResult> Download(Guid campaignId, CancellationToken ct = default)
//        {
//            var businessId = User.GetBusinessId();
//            if (businessId == Guid.Empty) return Unauthorized();

//            var schema = await BuildSchemaAsync(businessId, campaignId, ct);
//            if (!schema.Found) return NotFound();

//            // First row only: the header names the FE expects to see
//            var sb = new StringBuilder();
//            sb.AppendLine(string.Join(",", schema.Headers.Select(h =>
//                h.Contains(',') ? $"\"{h.Replace("\"", "\"\"")}\"" : h)));

//            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
//            var fileName = $"sample_{campaignId:N}.csv";
//            return File(bytes, "text/csv; charset=utf-8", fileName);
//        }

//        // -----------------------
//        // Helpers
//        // -----------------------



//        //private async Task<SchemaResult> BuildSchemaAsync(Guid businessId, Guid campaignId, CancellationToken ct)
//        //{
//        //    var campaign = await _db.Campaigns
//        //        .AsNoTracking()
//        //        .Where(c => c.Id == campaignId && c.BusinessId == businessId && !c.IsDeleted)
//        //        .Select(c => new
//        //        {
//        //            c.Id,
//        //            c.BusinessId,
//        //            c.TemplateId,
//        //            c.MessageTemplate,
//        //            c.Provider
//        //        })
//        //        .FirstOrDefaultAsync(ct);

//        //    if (campaign == null)
//        //        return new SchemaResult { Found = false };

//        //    var templateName =
//        //        !string.IsNullOrWhiteSpace(campaign.TemplateId) ? campaign.TemplateId! :
//        //        !string.IsNullOrWhiteSpace(campaign.MessageTemplate) ? campaign.MessageTemplate! :
//        //        string.Empty;

//        //    if (string.IsNullOrWhiteSpace(templateName))
//        //        return new SchemaResult { Found = true, Headers = new List<string> { "phone" }, PlaceholderCount = 0 };

//        //    var provider = (campaign.Provider ?? "META").ToUpperInvariant();

//        //    // 1) Normalized meta (may be sparse on some branches)
//        //    var meta = await _tpl.GetTemplateMetaAsync(
//        //        campaign.BusinessId,
//        //        templateName,
//        //        language: null,
//        //        provider: provider
//        //    );

//        //    // 2) Raw DB row fallback (has ButtonsJson / HasImageHeader / PlaceholderCount)
//        //    var tplRow = await _db.WhatsAppTemplates
//        //        .AsNoTracking()
//        //        .Where(w => w.BusinessId == businessId
//        //                    && w.IsActive
//        //                    && w.Name == templateName
//        //                    && w.Provider == provider)
//        //        .OrderByDescending(w => (w.UpdatedAt > w.CreatedAt ? w.UpdatedAt : w.CreatedAt))
//        //        .FirstOrDefaultAsync(ct);

//        //    var headers = new List<string> { "phone" };
//        //    int bodyCount = 0;

//        //    // ---------- BODY PLACEHOLDERS ----------
//        //    if (meta?.BodyPlaceholders != null && meta.BodyPlaceholders.Count > 0)
//        //    {
//        //        bodyCount = meta.BodyPlaceholders.Count;
//        //    }
//        //    else if (tplRow?.PlaceholderCount is int pc && pc > 0)
//        //    {
//        //        bodyCount = pc;
//        //    }
//        //    else
//        //    {
//        //        // try to infer from body text if available in meta
//        //        var bodyText = meta?.GetType()?.GetProperty("Body")?.GetValue(meta) as string;
//        //        if (!string.IsNullOrWhiteSpace(bodyText))
//        //        {
//        //            var m = System.Text.RegularExpressions.Regex.Matches(bodyText, @"\{\{\s*(\d+)\s*\}\}");
//        //            if (m.Count > 0)
//        //                bodyCount = m.Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();
//        //        }
//        //    }
//        //    for (int i = 1; i <= bodyCount; i++)
//        //        headers.Add($"body.{i}");

//        //    // ---------- HEADER MEDIA ----------
//        //    string headerType = (meta?.HeaderType ?? string.Empty).ToUpperInvariant();
//        //    if (string.IsNullOrEmpty(headerType))
//        //    {
//        //        // legacy bool
//        //        var hasImg = meta?.GetType()?.GetProperty("HasHeaderMedia")?.GetValue(meta) as bool?
//        //                     ?? meta?.GetType()?.GetProperty("HasImageHeader")?.GetValue(meta) as bool?;
//        //        if (hasImg == true) headerType = "IMAGE";
//        //        // row fallback?
//        //        if (string.IsNullOrEmpty(headerType) && tplRow?.HasImageHeader == true) headerType = "IMAGE";
//        //    }

//        //    if (headerType == "IMAGE") headers.Add("header.image_url");
//        //    if (headerType == "VIDEO") headers.Add("header.video_url");
//        //    if (headerType == "DOCUMENT" || headerType == "PDF") headers.Add("header.document_url");

//        //    // ---------- DYNAMIC URL BUTTONS ----------
//        //    // Match FE logic: a button needs a CSV value if:
//        //    //  - it's a URL-like button (type/subType says url) AND
//        //    //  - the URL contains a {{…}} placeholder OR parameters array contains a {{…}} text
//        //    var dynamicPositions = new SortedSet<int>();

//        //    bool LooksDynamic(string? val) =>
//        //    !string.IsNullOrEmpty(val) && val.Contains("{{", StringComparison.Ordinal);

//        //    bool IsUrlish(string? type, string? subType)
//        //    {
//        //        type = (type ?? string.Empty).ToLowerInvariant();
//        //        subType = (subType ?? string.Empty).ToLowerInvariant();
//        //        return type == "url" || subType == "url";
//        //    }


//        //    int GetPos(object b, int fallbackOneBased)
//        //    {
//        //        var t = b.GetType();
//        //        if (t.GetProperty("Index")?.GetValue(b) is int idx && idx > 0) return idx;
//        //        if (t.GetProperty("Order")?.GetValue(b) is int ord && ord >= 0) return ord + 1;
//        //        if (t.GetProperty("Position")?.GetValue(b) is int pos && pos > 0) return pos;
//        //        return fallbackOneBased;
//        //    }

//        //    // A) meta.Buttons
//        //    if (meta?.Buttons != null && meta.Buttons.Count > 0)
//        //    {
//        //        for (int i = 0; i < meta.Buttons.Count && dynamicPositions.Count < 3; i++)
//        //        {
//        //            var b = meta.Buttons[i];
//        //            var type = b.Type;
//        //            var subType = b.GetType().GetProperty("SubType")?.GetValue(b) as string; // if present
//        //            var value = b.Value;

//        //            if (IsUrlish(type, subType) && LooksDynamic(value))
//        //            {
//        //                var pos = GetPos(b, i + 1);
//        //                if (pos >= 1 && pos <= 3) dynamicPositions.Add(pos);
//        //            }
//        //        }
//        //    }

