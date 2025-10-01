// 📄 Features/TemplateCatalog/Services/TemplateSyncService.cs
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using xbytechat.api;
using xbytechat_api.WhatsAppSettings.Models;
using xbytechat.api.WhatsAppSettings.Providers;
using xbytechat.api.WhatsAppSettings.Abstractions;
using xbytechat.api.AuthModule.Models;

public record TemplateSyncResult(int Added, int Updated, int Skipped, DateTime SyncedAt);

public interface ITemplateSyncService
{
    /// <summary>
    /// Sync templates for a business. When force=false, a 12h TTL short-circuit is applied.
    /// Use force=true for the "Sync Template" button to bypass TTL.
    /// </summary>
    Task<TemplateSyncResult> SyncBusinessTemplatesAsync(Guid businessId, bool force = false, CancellationToken ct = default);
}

public sealed class TemplateSyncService : ITemplateSyncService
{
    private readonly AppDbContext _db;
    private readonly MetaTemplateCatalogProvider _meta;
    private readonly PinnacleTemplateCatalogProvider _pinnacle;
    private readonly ILogger<TemplateSyncService> _log;

    // Background/automatic runs are TTL-gated; manual button should call with force=true.
    private static readonly TimeSpan TTL = TimeSpan.FromHours(12);

    public TemplateSyncService(
        AppDbContext db,
        MetaTemplateCatalogProvider meta,
        PinnacleTemplateCatalogProvider pinnacle,
        ILogger<TemplateSyncService> log)
    {
        _db = db; _meta = meta; _pinnacle = pinnacle; _log = log;
    }

    public async Task<TemplateSyncResult> SyncBusinessTemplatesAsync(Guid businessId, bool force = false, CancellationToken ct = default)
    {
        var setting = await _db.WhatsAppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive, ct)
            ?? throw new InvalidOperationException("Active WhatsApp settings not found.");

        var now = DateTime.UtcNow;

        // ----- TTL short-circuit (only when NOT forced) -----
        if (!force)
        {
            var recent = await _db.WhatsAppTemplates
                .AsNoTracking()
                .Where(t => t.BusinessId == businessId)
                .OrderByDescending(t => t.LastSyncedAt)
                .Select(t => t.LastSyncedAt)
                .FirstOrDefaultAsync(ct);

            if (recent != default && now - recent < TTL)
            {
                _log.LogInformation("⏭️ Skipping sync for {BusinessId}; TTL not expired.", businessId);
                return new TemplateSyncResult(0, 0, 0, recent);
            }
        }

        // ----- Resolve provider & fetch all templates (ensure your provider LIST paginates) -----
        var providerKey = (setting.Provider ?? "meta_cloud").Trim().ToLowerInvariant();

        IReadOnlyList<TemplateCatalogItem> incoming = providerKey switch
        {
            "meta_cloud" => await _meta.ListAsync(setting, ct),   // should page through all results
            "pinnacle" => await _pinnacle.ListAsync(setting, ct),
            _ => Array.Empty<TemplateCatalogItem>()
        };

        // Fast exit on empty provider response (don’t archive everything on a transient provider issue)
        if (incoming == null) incoming = Array.Empty<TemplateCatalogItem>();

        // ----- Load existing once (fast) -----
        var existing = await _db.WhatsAppTemplates
            .Where(t => t.BusinessId == businessId && t.Provider == providerKey)
            .ToListAsync(ct);

        // Index by ExternalId when present; fallback key = $"{Name}::{Language}"
        var byExtId = existing
            .Where(e => !string.IsNullOrWhiteSpace(e.ExternalId))
            .ToDictionary(e => e.ExternalId!, e => e);

        static string NLKey(string name, string? lang) => $"{name}::{(lang ?? "").Trim().ToLowerInvariant()}";

        var byNameLang = existing.ToDictionary(
            e => NLKey(e.Name, e.Language),
            e => e,
            StringComparer.Ordinal);

        int added = 0, updated = 0, unchanged = 0;

        // Track “seen” to support optional archival
        var seenExtIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNLKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var it in incoming)
        {
            ct.ThrowIfCancellationRequested();

            var extId = it.ExternalId?.Trim();
            var nlKey = NLKey(it.Name, it.Language);

            seenNLKeys.Add(nlKey);
            if (!string.IsNullOrWhiteSpace(extId)) seenExtIds.Add(extId);

            var buttonsJson = JsonConvert.SerializeObject(it.Buttons);

            WhatsAppTemplate? row = null;

            // Prefer ExternalId match
            if (!string.IsNullOrWhiteSpace(extId) && byExtId.TryGetValue(extId, out var foundByExt))
            {
                row = foundByExt;
            }
            else if (byNameLang.TryGetValue(nlKey, out var foundByNL))
            {
                row = foundByNL;

                // If provider now returns an ExternalId, attach it so future runs match by ExternalId
                if (!string.IsNullOrWhiteSpace(extId) && string.IsNullOrWhiteSpace(row.ExternalId))
                {
                    row.ExternalId = extId;
                    updated++;
                }
            }

            if (row == null)
            {
                // INSERT
                var newRow = new WhatsAppTemplate
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    Provider = providerKey,
                    ExternalId = extId,
                    Name = it.Name,
                    Language = it.Language,
                    Status = string.IsNullOrWhiteSpace(it.Status) ? "APPROVED" : it.Status,
                    Category = it.Category,
                    Body = it.Body ?? "",
                    HasImageHeader = it.HasImageHeader,
                    PlaceholderCount = it.PlaceholderCount,
                    ButtonsJson = buttonsJson,
                    RawJson = it.RawJson,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastSyncedAt = now
                };

                _db.WhatsAppTemplates.Add(newRow);
                added++;

                // Update indexes so subsequent items in this batch see it
                if (!string.IsNullOrWhiteSpace(extId)) byExtId[extId] = newRow;
                byNameLang[nlKey] = newRow;
            }
            else
            {
                // UPDATE (only if something important changed)
                bool changed = false;

                string? newStatus = string.IsNullOrWhiteSpace(it.Status) ? row.Status : it.Status;
                string newBody = it.Body ?? row.Body ?? "";
                string newButtonsJson = buttonsJson ?? row.ButtonsJson ?? "";

                if (!string.Equals(row.Status, newStatus, StringComparison.Ordinal)) { row.Status = newStatus; changed = true; }
                if (!string.Equals(row.Category, it.Category, StringComparison.Ordinal)) { row.Category = it.Category; changed = true; }
                if (!string.Equals(row.Body ?? "", newBody, StringComparison.Ordinal)) { row.Body = newBody; changed = true; }
                if (row.HasImageHeader != it.HasImageHeader) { row.HasImageHeader = it.HasImageHeader; changed = true; }
                if (row.PlaceholderCount != it.PlaceholderCount) { row.PlaceholderCount = it.PlaceholderCount; changed = true; }
                if (!string.Equals(row.ButtonsJson ?? "", newButtonsJson, StringComparison.Ordinal)) { row.ButtonsJson = newButtonsJson; changed = true; }
                if (!string.IsNullOrWhiteSpace(it.RawJson) && !string.Equals(row.RawJson, it.RawJson, StringComparison.Ordinal))
                { row.RawJson = it.RawJson; changed = true; }

                // Always mark active + bump sync timestamp; bump UpdatedAt only when changed
                row.IsActive = true;
                row.LastSyncedAt = now;
                if (changed) { row.UpdatedAt = now; updated++; } else { unchanged++; }
            }
        }

        // ----- Optional: archive templates not returned this run (safe cleanup) -----
        // Only do this if provider returned at least 1 item (avoid mass-archive on provider outage)
        if (incoming.Count > 0)
        {
            foreach (var e in existing)
            {
                // Match by ExternalId when present, else by Name+Language
                bool stillThere =
                    (!string.IsNullOrWhiteSpace(e.ExternalId) && seenExtIds.Contains(e.ExternalId)) ||
                    seenNLKeys.Contains(NLKey(e.Name, e.Language));

                if (!stillThere && e.IsActive)
                {
                    e.IsActive = false;
                    e.LastSyncedAt = now;
                    e.UpdatedAt = now;
                    updated++; // count as an update
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        return new TemplateSyncResult(added, updated, unchanged, now);
    }
}


//// 📄 Features/TemplateCatalog/Services/TemplateSyncService.cs
//using Microsoft.EntityFrameworkCore;
//using Newtonsoft.Json;
//using xbytechat.api.AuthModule.Models;
//using xbytechat.api;
//using xbytechat.api.WhatsAppSettings.Abstractions;
//using xbytechat.api.WhatsAppSettings.Providers;
//using xbytechat_api.WhatsAppSettings.Models;

//public record TemplateSyncResult(int Added, int Updated, int Skipped, DateTime SyncedAt);

//public interface ITemplateSyncService
//{
//    Task<TemplateSyncResult> SyncBusinessTemplatesAsync(Guid businessId, bool force = false, CancellationToken ct = default);
//}

//public sealed class TemplateSyncService : ITemplateSyncService
//{
//    private readonly AppDbContext _db;
//    private readonly MetaTemplateCatalogProvider _meta;
//    private readonly PinnacleTemplateCatalogProvider _pinnacle;
//    private readonly ILogger<TemplateSyncService> _log;

//    private static readonly TimeSpan TTL = TimeSpan.FromHours(12);

//    public TemplateSyncService(AppDbContext db,
//        MetaTemplateCatalogProvider meta,
//        PinnacleTemplateCatalogProvider pinnacle,
//        ILogger<TemplateSyncService> log)
//    { _db = db; _meta = meta; _pinnacle = pinnacle; _log = log; }

//    public async Task<TemplateSyncResult> SyncBusinessTemplatesAsync(Guid businessId, bool force = false, CancellationToken ct = default)
//    {
//        var setting = await _db.WhatsAppSettings.FirstOrDefaultAsync(x => x.BusinessId == businessId && x.IsActive, ct)
//                      ?? throw new InvalidOperationException("Active WhatsApp settings not found.");

//        var now = DateTime.UtcNow;

//        // TTL short-circuit
//        if (!force)
//        {
//            var recent = await _db.WhatsAppTemplates
//                .Where(t => t.BusinessId == businessId)
//                .OrderByDescending(t => t.LastSyncedAt)
//                .Select(t => t.LastSyncedAt)
//                .FirstOrDefaultAsync(ct);

//            if (recent != default && now - recent < TTL)
//            {
//                _log.LogInformation("⏭️ Skipping sync for {BusinessId}; TTL not expired.", businessId);
//                return new TemplateSyncResult(0, 0, 0, recent);
//            }
//        }

//        var providerKey = (setting.Provider ?? "meta_cloud").Trim().ToLowerInvariant();
//        IReadOnlyList<TemplateCatalogItem> incoming = providerKey switch
//        {
//            "meta_cloud" => await _meta.ListAsync(setting, ct),
//            "pinnacle" => await _pinnacle.ListAsync(setting, ct),
//            _ => Array.Empty<TemplateCatalogItem>()
//        };

//        int added = 0, updated = 0, skipped = 0;

//        foreach (var it in incoming)
//        {
//            var existing = await _db.WhatsAppTemplates.FirstOrDefaultAsync(t =>
//                t.BusinessId == businessId &&
//                t.Provider == providerKey &&
//                t.Name == it.Name &&
//                t.Language == it.Language, ct);

//            var buttonsJson = JsonConvert.SerializeObject(it.Buttons);

//            if (existing == null)
//            {
//                await _db.WhatsAppTemplates.AddAsync(new WhatsAppTemplate
//                {
//                    BusinessId = businessId,
//                    Provider = providerKey,
//                    ExternalId = it.ExternalId,
//                    Name = it.Name,
//                    Language = it.Language,
//                    Status = string.IsNullOrWhiteSpace(it.Status) ? "APPROVED" : it.Status,
//                    Category = it.Category,
//                    Body = it.Body ?? "",
//                    HasImageHeader = it.HasImageHeader,
//                    PlaceholderCount = it.PlaceholderCount,
//                    ButtonsJson = buttonsJson,
//                    RawJson = it.RawJson,
//                    LastSyncedAt = now,
//                    CreatedAt = now,
//                    UpdatedAt = now,
//                    IsActive = true
//                }, ct);
//                added++;
//            }
//            else
//            {
//                existing.ExternalId = it.ExternalId ?? existing.ExternalId;
//                existing.Status = string.IsNullOrWhiteSpace(it.Status) ? existing.Status : it.Status;
//                existing.Category = it.Category ?? existing.Category;
//                existing.Body = it.Body ?? existing.Body;
//                existing.HasImageHeader = it.HasImageHeader;
//                existing.PlaceholderCount = it.PlaceholderCount;
//                existing.ButtonsJson = buttonsJson;
//                existing.RawJson = it.RawJson ?? existing.RawJson;
//                existing.LastSyncedAt = now;
//                existing.UpdatedAt = now;
//                existing.IsActive = true;
//                updated++;
//            }
//        }

//        await _db.SaveChangesAsync(ct);

//        return new TemplateSyncResult(added, updated, skipped, now);
//    }
//}
