using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using xbytechat.api.Features.WhatsAppSettings.Models;
using xbytechat.api.Features.WhatsAppSettings.Services;
using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat.api.Features.WhatsAppSettings.Services
{
    public sealed class WhatsAppPhoneNumberService : IWhatsAppPhoneNumberService
    {
        private readonly AppDbContext _db;

        public WhatsAppPhoneNumberService(AppDbContext db)
        {
            _db = db;
        }

        //public async Task<IReadOnlyList<WhatsAppPhoneNumber>> ListAsync(Guid businessId, string provider)
        //{
        //    var prov = provider?.Trim() ?? string.Empty;
        //    return await _db.WhatsAppPhoneNumbers
        //        .Where(x => x.BusinessId == businessId && x.Provider.ToLower() == prov.ToLower())
        //        .OrderByDescending(x => x.IsDefault)
        //        .ThenBy(x => x.WhatsAppBusinessNumber)
        //        .ToListAsync();
        //}
        public async Task<IReadOnlyList<WhatsAppPhoneNumber>> ListAsync(
    Guid businessId,
    string provider,
    CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(provider))
                throw new ArgumentException("Provider is required.", nameof(provider));

            // Enforce your uppercase-only contract (no normalization here)
            if (provider is not "PINNACLE" and not "META_CLOUD")
                throw new ArgumentOutOfRangeException(nameof(provider),
                    "Provider must be exactly 'PINNACLE' or 'META_CLOUD'.");

            var list = await _db.WhatsAppPhoneNumbers
                .AsNoTracking()
                .Where(x => x.BusinessId == businessId && x.Provider == provider) // exact, case-sensitive
                .OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.WhatsAppBusinessNumber)
                .ToListAsync(ct);

            return list; // List<T> implements IReadOnlyList<T>
        }

        private static string NormalizeProvider(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Pinnacle";
            var s = raw.Trim();
            if (string.Equals(s, "Pinnacle", StringComparison.OrdinalIgnoreCase)) return "Pinnacle";
            if (string.Equals(s, "Meta_cloud", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "meta_cloud", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "meta", StringComparison.OrdinalIgnoreCase))
                return "Meta_cloud";
            return s;
        }



        //public async Task<WhatsAppPhoneNumber> UpsertAsync(
        //    Guid businessId,
        //    string provider,
        //    string phoneNumberId,
        //    string whatsAppBusinessNumber,
        //    string? senderDisplayName,
        //    bool? isActive = null,
        //    bool? isDefault = null)
        //    {
        //        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("provider is required");
        //        if (string.IsNullOrWhiteSpace(phoneNumberId)) throw new ArgumentException("phoneNumberId is required");
        //        if (string.IsNullOrWhiteSpace(whatsAppBusinessNumber)) throw new ArgumentException("whatsAppBusinessNumber is required");

        //        var prov = NormalizeProvider(provider);
        //        var provLc = prov.ToLowerInvariant();
        //        var now = DateTime.UtcNow;

        //        // DEBUG LOG (temporary): see what we’re matching
        //        Console.WriteLine($"[Upsert] biz={businessId} provider={prov}");

        //        // 1) Case/whitespace-insensitive match for settings
        //        var setting = await _db.WhatsAppSettings
        //            .FirstOrDefaultAsync(s =>
        //                s.BusinessId == businessId &&
        //                s.Provider.ToLower() == provLc);        // <= robust match

        //        // Optional extra safety: if still null, try a trimmed “like” query
        //        if (setting == null)
        //        {
        //            setting = await _db.WhatsAppSettings
        //                .FromSqlRaw(
        //                    @"select * from ""WhatsAppSettings""
        //                  where ""BusinessId"" = {0}
        //                    and lower(trim(""Provider"")) = {1}", businessId, provLc)
        //                .AsNoTracking()
        //                .FirstOrDefaultAsync();
        //            if (setting != null)
        //            {
        //                // reattach tracked entity
        //                setting = await _db.WhatsAppSettings
        //                    .FirstAsync(s => s.Id == setting.Id);
        //            }
        //        }

        //        if (setting == null)
        //        {
        //            // STUB: satisfy NOT NULLs (ApiUrl was failing before)
        //            setting = new WhatsAppSettingEntity
        //            {
        //                Id = Guid.NewGuid(),
        //                BusinessId = businessId,
        //                Provider = prov,
        //                ApiKey = string.Empty,
        //                ApiUrl = string.Empty,                 // <-- important if column is NOT NULL
        //                IsActive = true,
        //                CreatedAt = now
        //            };
        //            _db.WhatsAppSettings.Add(setting);
        //            Console.WriteLine($"[Upsert] created stub settings for biz={businessId} provider={prov}");
        //        }

        //        // 2) Upsert the number (same robust provider compare)
        //        var entity = await _db.WhatsAppPhoneNumbers
        //            .FirstOrDefaultAsync(x =>
        //                x.BusinessId == businessId &&
        //                x.Provider.ToLower() == provLc &&
        //                x.PhoneNumberId == phoneNumberId);

        //        if (entity == null)
        //        {
        //            entity = new WhatsAppPhoneNumber
        //            {
        //                Id = Guid.NewGuid(),
        //                BusinessId = businessId,
        //                Provider = prov,
        //                PhoneNumberId = phoneNumberId,
        //                WhatsAppBusinessNumber = whatsAppBusinessNumber,
        //                SenderDisplayName = senderDisplayName,
        //                IsActive = isActive ?? true,
        //                IsDefault = isDefault ?? false,
        //                CreatedAt = now
        //            };
        //            _db.WhatsAppPhoneNumbers.Add(entity);
        //        }
        //        else
        //        {
        //            entity.WhatsAppBusinessNumber = whatsAppBusinessNumber;
        //            entity.SenderDisplayName = senderDisplayName;
        //            if (isActive.HasValue) entity.IsActive = isActive.Value;
        //            if (isDefault.HasValue) entity.IsDefault = isDefault.Value;
        //            entity.UpdatedAt = now;
        //        }

        //        var setDefault = (isDefault == true) || entity.IsDefault;

        //        await using var tx = await _db.Database.BeginTransactionAsync();
        //        try
        //        {
        //            await _db.SaveChangesAsync();

        //            if (setDefault)
        //            {
        //                await _db.WhatsAppPhoneNumbers
        //                    .Where(x => x.BusinessId == businessId &&
        //                                x.Provider.ToLower() == provLc &&
        //                                x.Id != entity.Id)
        //                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));

        //                // Mirror legacy columns
        //                setting.PhoneNumberId = entity.PhoneNumberId;
        //                setting.WhatsAppBusinessNumber = entity.WhatsAppBusinessNumber;
        //                setting.UpdatedAt = now;
        //                await _db.SaveChangesAsync();
        //            }

        //            await tx.CommitAsync();
        //        }
        //        catch (DbUpdateException ex)
        //        {
        //            await tx.RollbackAsync();
        //            var root = ex.InnerException?.Message ?? ex.Message;
        //            throw new InvalidOperationException($"Failed to save WhatsApp number: {root}", ex);
        //        }

        //        return entity;
        //    }

        public async Task<WhatsAppPhoneNumber> UpsertAsync(
            Guid businessId, string provider, string phoneNumberId, string whatsAppBusinessNumber,
            string? senderDisplayName, bool? isActive = null, bool? isDefault = null)
        {
            if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("provider is required");
            if (string.IsNullOrWhiteSpace(phoneNumberId)) throw new ArgumentException("phoneNumberId is required");
            if (string.IsNullOrWhiteSpace(whatsAppBusinessNumber)) throw new ArgumentException("whatsAppBusinessNumber is required");

            var now = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync();

            // 1) Ensure parent exists (EXACT match)
            var setting = await _db.WhatsAppSettings
                .FirstOrDefaultAsync(s => s.BusinessId == businessId && s.Provider == provider);

            if (setting == null)
            {
                setting = new WhatsAppSettingEntity
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    Provider = provider,           // 'META_CLOUD' | 'PINNACLE'
                    ApiUrl = string.Empty,         // keep NOT NULL-safe
                    ApiKey = string.Empty,
                    IsActive = true,
                    CreatedAt = now
                };
                _db.WhatsAppSettings.Add(setting);
                await _db.SaveChangesAsync();     // FLUSH parent BEFORE child (prevents 23503)
            }

            var providerForChild = setting.Provider; // ensure FK matches exactly

            // 2) Upsert the number
            var entity = await _db.WhatsAppPhoneNumbers.FirstOrDefaultAsync(x =>
                x.BusinessId == businessId && x.Provider == providerForChild && x.PhoneNumberId == phoneNumberId);

            if (entity == null)
            {
                entity = new WhatsAppPhoneNumber
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    Provider = providerForChild,
                    PhoneNumberId = phoneNumberId,
                    WhatsAppBusinessNumber = whatsAppBusinessNumber,
                    SenderDisplayName = senderDisplayName,
                    IsActive = isActive ?? true,
                    IsDefault = isDefault ?? false,
                    CreatedAt = now
                };
                _db.WhatsAppPhoneNumbers.Add(entity);
            }
            else
            {
                entity.WhatsAppBusinessNumber = whatsAppBusinessNumber;
                entity.SenderDisplayName = senderDisplayName;
                if (isActive.HasValue) entity.IsActive = isActive.Value;
                if (isDefault.HasValue) entity.IsDefault = isDefault.Value;
                entity.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(); // child saved

            // 3) Default + mirror
            if (isDefault == true || entity.IsDefault)
            {
                await _db.WhatsAppPhoneNumbers
                    .Where(x => x.BusinessId == businessId && x.Provider == providerForChild && x.Id != entity.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));

                setting.PhoneNumberId = entity.PhoneNumberId;
                setting.WhatsAppBusinessNumber = entity.WhatsAppBusinessNumber;
                setting.UpdatedAt = now;
                await _db.SaveChangesAsync();
            }

            await tx.CommitAsync();
            return entity;
        }


        public async Task<bool> DeleteAsync(Guid businessId, string provider, Guid id)
        {
            var prov = provider?.Trim() ?? string.Empty;

            var entity = await _db.WhatsAppPhoneNumbers
                .FirstOrDefaultAsync(x => x.Id == id && x.BusinessId == businessId && x.Provider.ToLower() == prov.ToLower());

            if (entity == null) return false;

            _db.WhatsAppPhoneNumbers.Remove(entity);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> SetDefaultAsync(Guid businessId, string provider, Guid id)
        {
            var prov = provider?.Trim() ?? string.Empty;

            await _db.Database.BeginTransactionAsync();
            try
            {
                // ensure target exists and belongs to (business, provider)
                var target = await _db.WhatsAppPhoneNumbers
                    .FirstOrDefaultAsync(x => x.Id == id && x.BusinessId == businessId && x.Provider.ToLower() == prov.ToLower());
                if (target == null) return false;

                await _db.WhatsAppPhoneNumbers
                    .Where(x => x.BusinessId == businessId && x.Provider.ToLower() == prov.ToLower())
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));

                target.IsDefault = true;
                target.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                await _db.Database.CommitTransactionAsync();
                return true;
            }
            catch
            {
                await _db.Database.RollbackTransactionAsync();
                throw;
            }
        }

        public async Task<WhatsAppPhoneNumber?> FindAsync(Guid businessId, string provider, string phoneNumberId)
        {
            var prov = provider?.Trim() ?? string.Empty;

            return await _db.WhatsAppPhoneNumbers
                .FirstOrDefaultAsync(x =>
                    x.BusinessId == businessId &&
                    x.Provider.ToLower() == prov.ToLower() &&
                    x.PhoneNumberId == phoneNumberId);
        }

        public async Task<WhatsAppPhoneNumber?> GetDefaultAsync(Guid businessId, string provider)
        {
            var prov = provider?.Trim() ?? string.Empty;

            // covered by partial unique index: at most one IsDefault per (biz, provider)
            return await _db.WhatsAppPhoneNumbers
                .FirstOrDefaultAsync(x =>
                    x.BusinessId == businessId &&
                    x.Provider.ToLower() == prov.ToLower() &&
                    x.IsDefault);
        }
    }
}
