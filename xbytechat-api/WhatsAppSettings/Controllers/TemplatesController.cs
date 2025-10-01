using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileSystemGlobbing;
using xbytechat_api.WhatsAppSettings.Models;
using xbytechat_api.WhatsAppSettings.Services;
namespace xbytechat.api.WhatsAppSettings.Controllers
{
    [ApiController]
    [Route("api/templates")]
    public class TemplatesController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ITemplateSyncService _sync;
        private readonly IWhatsAppTemplateFetcherService _fetcher;

        public TemplatesController(AppDbContext db, ITemplateSyncService sync, IWhatsAppTemplateFetcherService fetcher)
        { _db = db; _sync = sync; _fetcher = fetcher; }

        [HttpPost("sync/{businessId:guid}")]
        [Authorize]
        public async Task<IActionResult> Sync(Guid businessId, [FromQuery] bool force = false)
        {
            if (businessId == Guid.Empty) return BadRequest(new { success = false, message = "Invalid businessId" });
            var result = await _sync.SyncBusinessTemplatesAsync(businessId, force);
            return Ok(new { success = true, result });
        }

        [HttpGet("{businessId:guid}")]
        [Authorize]
        public async Task<IActionResult> List(Guid businessId, [FromQuery] string? q = null,
            [FromQuery] string? status = "APPROVED", [FromQuery] string? language = null,
            [FromQuery] string? provider = null)
        {
            var query = _db.WhatsAppTemplates.AsNoTracking()
                .Where(x => x.BusinessId == businessId && x.IsActive);

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(x => x.Status == status);

            if (!string.IsNullOrWhiteSpace(language))
                query = query.Where(x => x.Language == language);

            if (!string.IsNullOrWhiteSpace(provider))
                query = query.Where(x => x.Provider == provider);

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(x => x.Name.Contains(q) || x.Body.Contains(q));

            var items = await query
                .OrderBy(x => x.Name)
                .Select(x => new
                {
                    x.Name,
                    x.Language,
                    x.Status,
                    x.Category,
                    x.PlaceholderCount,
                    x.HasImageHeader,
                    x.ButtonsJson
                })
                .ToListAsync();

            return Ok(new { success = true, templates = items });
        }

        [HttpGet("{businessId:guid}/{name}")]
        [Authorize]
        //public async Task<IActionResult> GetOne(Guid businessId, string name, [FromQuery] string? language = null)
        //{
        //    var tpl = await _db.WhatsAppTemplates.AsNoTracking()
        //        .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.Name == name &&
        //                                  (language == null || x.Language == language));
        //    if (tpl == null) return NotFound();
        //    return Ok(new
        //    {
        //        tpl.Name,
        //        tpl.Language,
        //        tpl.Status,
        //        tpl.Category,
        //        tpl.Body,
        //        tpl.PlaceholderCount,
        //        tpl.HasImageHeader,
        //        tpl.ButtonsJson
        //    });
        //}

        public async Task<IActionResult> GetOne(Guid businessId, string name, [FromQuery] string? language = null)
        {
            var tpl = await _db.WhatsAppTemplates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.Name == name &&
                                          (language == null || x.Language == language));
            if (tpl == null) return NotFound();

            // 🔎 Ask meta service for precise header info (covers IMAGE/VIDEO/DOCUMENT/TEXT/none)
            string headerKind = "none";
            bool requiresHeaderMediaUrl = false;
            try
            {
                var meta = await _fetcher.GetTemplateMetaAsync(businessId, tpl.Name, tpl.Language, provider: null);
                var ht = meta?.HeaderType?.ToUpperInvariant();
                headerKind = ht switch
                {
                    "IMAGE" => "image",
                    "VIDEO" => "video",
                    "DOCUMENT" => "document",
                    "TEXT" => "text",
                    _ => (tpl.HasImageHeader ? "image" : "none")
                };
                requiresHeaderMediaUrl = headerKind is "image" or "video" or "document";
            }
            catch
            {
                // fallback to legacy flag
                headerKind = tpl.HasImageHeader ? "image" : "none";
                requiresHeaderMediaUrl = headerKind == "image";
            }

            return Ok(new
            {
                tpl.Name,
                tpl.Language,
                tpl.Status,
                tpl.Category,
                tpl.Body,
                tpl.PlaceholderCount,
                tpl.HasImageHeader,
                tpl.ButtonsJson,
                headerKind,                // 👈 NEW for UI
                requiresHeaderMediaUrl     // 👈 NEW for UI
            });
        }
    }
}
