using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Serilog;
using xbytechat.api;
using xbytechat.api.Features.CampaignModule.DTOs;
using xbytechat.api.Features.CampaignModule.Models;

namespace xbytechat.api.Features.CampaignModule.Services
{
    public class CsvBatchService : ICsvBatchService
    {
        private readonly AppDbContext _db;

        public CsvBatchService(AppDbContext db)
        {
            _db = db;
        }

        // ----------------------------
        // Upload + ingest
        // ----------------------------
        public async Task<CsvBatchUploadResultDto> CreateAndIngestAsync(
            Guid businessId,
            string fileName,
            Stream stream,
            Guid? audienceId = null,
            CancellationToken ct = default)
        {
            // 1) Create batch shell
            var batch = new CsvBatch
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                AudienceId = audienceId,          // nullable by design (ok if null)
                FileName = fileName,
                CreatedAt = DateTime.UtcNow,
                Status = "ingesting",
                RowCount = 0,
                SkippedCount = 0,
                HeadersJson = null
            };

            _db.CsvBatches.Add(batch);
            await _db.SaveChangesAsync(ct);

            try
            {
                // 2) Parse headers + rows (robust CSV parsing)
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);

                string? headerLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(headerLine))
                {
                    // No header line → fall back to single "phone" column
                    var headers = new List<string> { "phone" };
                    batch.HeadersJson = JsonSerializer.Serialize(headers);
                    batch.Status = "ready";
                    await _db.SaveChangesAsync(ct);

                    Log.Warning("CSV had no header line. Created batch {BatchId} with fallback 'phone' header.", batch.Id);

                    return new CsvBatchUploadResultDto
                    {
                        BatchId = batch.Id,
                        AudienceId = batch.AudienceId,
                        FileName = batch.FileName ?? string.Empty,
                        RowCount = 0,
                        Headers = headers
                    };
                }

                var delim = DetectDelimiter(headerLine);
                var headersParsed = ParseCsvLine(headerLine, delim)
                    .Select(h => h.Trim())
                    .Where(h => !string.IsNullOrEmpty(h))
                    .ToList();

                if (headersParsed.Count == 0)
                    headersParsed = new List<string> { "phone" };

                batch.HeadersJson = JsonSerializer.Serialize(headersParsed);
                await _db.SaveChangesAsync(ct);

                // 3) Stream rows into CsvRows
                var rowsBuffer = new List<CsvRow>(capacity: 1024);
                int rowIndex = 0;

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cols = ParseCsvLine(line, delim);

                    var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < headersParsed.Count; i++)
                    {
                        var key = headersParsed[i];
                        var val = i < cols.Count ? cols[i]?.Trim() : null;
                        dict[key] = val;
                    }

                    rowsBuffer.Add(new CsvRow
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,           // IMPORTANT for later queries
                        BatchId = batch.Id,
                        RowIndex = rowIndex++,
                        DataJson = JsonSerializer.Serialize(dict)
                    });

                    if (rowsBuffer.Count >= 1000)
                    {
                        _db.CsvRows.AddRange(rowsBuffer);
                        await _db.SaveChangesAsync(ct);
                        rowsBuffer.Clear();
                    }
                }

                if (rowsBuffer.Count > 0)
                {
                    _db.CsvRows.AddRange(rowsBuffer);
                    await _db.SaveChangesAsync(ct);
                    rowsBuffer.Clear();
                }

                batch.RowCount = rowIndex;
                batch.Status = "ready";
                await _db.SaveChangesAsync(ct);

                Log.Information("CsvBatch {BatchId} ingested: {Rows} rows; headers={HeaderCount}", batch.Id, batch.RowCount, headersParsed.Count);

                return new CsvBatchUploadResultDto
                {
                    BatchId = batch.Id,
                    AudienceId = batch.AudienceId,
                    FileName = batch.FileName ?? string.Empty,
                    RowCount = batch.RowCount,
                    Headers = headersParsed
                };
            }
            catch (Exception ex)
            {
                batch.Status = "failed";
                batch.ErrorMessage = ex.Message;
                await _db.SaveChangesAsync(ct);
                Log.Error(ex, "CSV ingest failed for batch {BatchId}", batch.Id);
                throw;
            }
        }

        // ----------------------------
        // Batch info
        // ----------------------------
        private async Task<CsvBatchUploadResultDto> IngestCoreAsync(
            Guid businessId,
            string fileName,
            Stream stream,
            CancellationToken ct)
        {
            // Minimal “stage only” helper (kept in case other code calls it)
            var batch = new CsvBatch
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                FileName = fileName,
                CreatedAt = DateTime.UtcNow,
                Status = "ready",
                HeadersJson = null,
                RowCount = 0,
                SkippedCount = 0,
                ErrorMessage = null
            };
            _db.CsvBatches.Add(batch);
            await _db.SaveChangesAsync(ct);

            Log.Information("CsvBatch {BatchId} staged for business {Biz}", batch.Id, businessId);

            return new CsvBatchUploadResultDto
            {
                BatchId = batch.Id,
                AudienceId = null,
                FileName = fileName,
                RowCount = 0,
                Headers = new List<string>(),
                Message = "CSV batch created."
            };
        }

        public async Task<CsvBatchInfoDto?> GetBatchAsync(Guid businessId, Guid batchId, CancellationToken ct = default)
        {
            var batch = await _db.CsvBatches
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == batchId && b.BusinessId == businessId, ct);

            if (batch == null) return null;

            var headers = SafeParseHeaderArray(batch.HeadersJson);

            return new CsvBatchInfoDto
            {
                BatchId = batch.Id,
                AudienceId = batch.AudienceId,
                RowCount = batch.RowCount,
                Headers = headers,
                CreatedAt = batch.CreatedAt
            };
        }

        // ----------------------------
        // Samples (single implementation)
        // ----------------------------
        public async Task<IReadOnlyList<CsvRowSampleDto>> GetSamplesAsync(
            Guid businessId,
            Guid batchId,
            int take = 20,
            CancellationToken ct = default)
        {
            if (take <= 0) take = 20;
            if (take > 100) take = 100;

            var batch = await _db.CsvBatches
                .AsNoTracking()
                .Where(b => b.Id == batchId && b.BusinessId == businessId)
                .Select(b => new { b.Id, b.HeadersJson, b.RowCount })
                .FirstOrDefaultAsync(ct);

            if (batch is null)
                throw new KeyNotFoundException("Batch not found.");

            // If no rows yet, return empty samples gracefully
            if (batch.RowCount <= 0)
                return Array.Empty<CsvRowSampleDto>();

            var headerList = SafeParseHeaderArray(batch.HeadersJson);

            var rows = await _db.CsvRows
                .AsNoTracking()
                .Where(r => r.BusinessId == businessId && r.BatchId == batchId)
                .OrderBy(r => r.RowIndex)
                .Take(take)
                .Select(r => new { r.RowIndex, r.DataJson })
                .ToListAsync(ct);

            var result = new List<CsvRowSampleDto>(rows.Count);
            foreach (var r in rows)
            {
                var dict = SafeParseDict(r.DataJson);

                // Ensure consistent header order (fill missing with null)
                var ordered = new Dictionary<string, string?>(headerList.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var h in headerList)
                {
                    dict.TryGetValue(h, out var v);
                    ordered[h] = v;
                }

                result.Add(new CsvRowSampleDto
                {
                    RowIndex = r.RowIndex,
                    Data = ordered
                });
            }

            return result;
        }

        // ----------------------------
        // List / Page / Delete / Validate
        // ----------------------------
        public async Task<List<CsvBatchListItemDto>> ListBatchesAsync(Guid businessId, int limit = 20, CancellationToken ct = default)
        {
            if (limit <= 0) limit = 20;
            if (limit > 100) limit = 100;

            return await _db.CsvBatches
                .AsNoTracking()
                .Where(b => b.BusinessId == businessId)
                .OrderByDescending(b => b.CreatedAt)
                .Take(limit)
                .Select(b => new CsvBatchListItemDto
                {
                    BatchId = b.Id,
                    FileName = b.FileName,
                    RowCount = b.RowCount,
                    Status = b.Status,
                    CreatedAt = b.CreatedAt
                })
                .ToListAsync(ct);
        }

        public async Task<CsvBatchRowsPageDto> GetRowsPageAsync(Guid businessId, Guid batchId, int skip, int take, CancellationToken ct = default)
        {
            if (take <= 0) take = 50;
            if (take > 200) take = 200;
            if (skip < 0) skip = 0;

            var exists = await _db.CsvBatches.AsNoTracking()
                .AnyAsync(b => b.Id == batchId && b.BusinessId == businessId, ct);
            if (!exists) throw new KeyNotFoundException("CSV batch not found.");

            var total = await _db.CsvRows.AsNoTracking()
                .Where(r => r.BusinessId == businessId && r.BatchId == batchId)
                .CountAsync(ct);

            var rows = await _db.CsvRows.AsNoTracking()
                .Where(r => r.BusinessId == businessId && r.BatchId == batchId)
                .OrderBy(r => r.RowIndex)
                .Skip(skip)
                .Take(take)
                .Select(r => new CsvRowSampleDto
                {
                    RowIndex = r.RowIndex,
                    Data = SafeParseDict(r.DataJson)
                })
                .ToListAsync(ct);

            return new CsvBatchRowsPageDto
            {
                BatchId = batchId,
                TotalRows = total,
                Skip = skip,
                Take = take,
                Rows = rows
            };
        }

        public async Task<bool> DeleteBatchAsync(Guid businessId, Guid batchId, CancellationToken ct = default)
        {
            var batch = await _db.CsvBatches
                .FirstOrDefaultAsync(b => b.Id == batchId && b.BusinessId == businessId, ct);

            if (batch == null) return false;

            using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var rows = _db.CsvRows.Where(r => r.BusinessId == businessId && r.BatchId == batchId);
                _db.CsvRows.RemoveRange(rows);

                _db.CsvBatches.Remove(batch);

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        private static readonly string[] PhoneHeaderCandidates =
        { "phone", "mobile", "whatsapp", "msisdn", "whatsapp_number", "contact", "contact_number" };

        public async Task<CsvBatchValidationResultDto> ValidateAsync(
            Guid businessId,
            Guid batchId,
            CsvBatchValidationRequestDto request,
            CancellationToken ct = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (request.SampleSize <= 0) request.SampleSize = 20;
            if (request.SampleSize > 100) request.SampleSize = 100;

            var batch = await _db.CsvBatches.AsNoTracking()
                .Where(b => b.BusinessId == businessId && b.Id == batchId)
                .Select(b => new { b.Id, b.HeadersJson, b.RowCount })
                .FirstOrDefaultAsync(ct);

            if (batch == null) throw new KeyNotFoundException("CSV batch not found.");

            var headers = SafeParseHeaderArray(batch.HeadersJson);
            var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);

            var result = new CsvBatchValidationResultDto
            {
                BatchId = batchId,
                TotalRows = batch.RowCount
            };

            // Required headers check
            if (request.RequiredHeaders != null && request.RequiredHeaders.Count > 0)
            {
                foreach (var req in request.RequiredHeaders)
                {
                    if (!headerSet.Contains(req))
                        result.MissingRequiredHeaders.Add(req);
                }

                if (result.MissingRequiredHeaders.Count > 0)
                    result.Errors.Add("Required headers are missing.");
            }

            // Determine phone field
            var phoneField = request.PhoneField;
            if (string.IsNullOrWhiteSpace(phoneField))
                phoneField = PhoneHeaderCandidates.FirstOrDefault(headerSet.Contains);

            result.PhoneField = phoneField;

            if (string.IsNullOrWhiteSpace(phoneField))
            {
                result.Errors.Add("No phone field provided or detected.");
                return result; // cannot scan rows without a phone column
            }

            // Scan rows for phone presence & duplicates
            var seenPhones = new HashSet<string>(StringComparer.Ordinal);
            var problemSamples = new List<CsvRowSampleDto>();

            var rowsQuery = _db.CsvRows.AsNoTracking()
                .Where(r => r.BusinessId == businessId && r.BatchId == batchId)
                .OrderBy(r => r.RowIndex)
                .Select(r => new { r.RowIndex, r.DataJson });

            await foreach (var row in rowsQuery.AsAsyncEnumerable().WithCancellation(ct))
            {
                var dict = SafeParseDict(row.DataJson);
                dict.TryGetValue(phoneField, out var rawPhone);

                var normalized = NormalizePhoneMaybe(rawPhone, request.NormalizePhones);

                var isProblem = false;

                if (string.IsNullOrWhiteSpace(normalized))
                {
                    result.MissingPhoneCount++;
                    isProblem = true;
                }
                else if (request.Deduplicate && !seenPhones.Add(normalized))
                {
                    result.DuplicatePhoneCount++;
                    isProblem = true;
                }

                if (isProblem && problemSamples.Count < request.SampleSize)
                {
                    problemSamples.Add(new CsvRowSampleDto
                    {
                        RowIndex = row.RowIndex,
                        Data = dict
                    });
                }
            }

            result.ProblemSamples = problemSamples;

            if (result.MissingPhoneCount > 0)
                result.Errors.Add("Some rows are missing phone numbers.");
            if (result.DuplicatePhoneCount > 0)
                result.Warnings.Add("Duplicate phone numbers detected (after normalization).");

            return result;
        }

        // ----------------------------
        // helpers
        // ----------------------------
        private static List<string> SafeParseHeaderArray(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new List<string>()
                    : (JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>());
            }
            catch { return new List<string>(); }
        }

        private static Dictionary<string, string?> SafeParseDict(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new Dictionary<string, string?>()
                    : (JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ??
                       new Dictionary<string, string?>());
            }
            catch { return new Dictionary<string, string?>(); }
        }

        private static char DetectDelimiter(string headerLine)
        {
            var candidates = new[] { ',', ';', '\t' };
            var counts = candidates.Select(c => (c, count: headerLine.Count(ch => ch == c))).ToList();
            var best = counts.OrderByDescending(x => x.count).First();
            return best.count > 0 ? best.c : ',';
        }

        /// <summary>
        /// CSV parser with delimiter support: handles commas/semicolons/tabs, double quotes,
        /// and escaped quotes (""). It does NOT support embedded newlines inside quoted fields.
        /// </summary>
        private static List<string> ParseCsvLine(string line, char delimiter)
        {
            var result = new List<string>();
            if (line == null) return result;

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Handle escaped quote ""
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == delimiter)
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            result.Add(sb.ToString());
            return result;
        }

        private static string? NormalizePhoneMaybe(string? raw, bool normalize)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw.Trim();
            if (!normalize) return trimmed;

            var digits = Regex.Replace(trimmed, "[^0-9]", "");
            digits = digits.TrimStart('0');

            // Heuristic for India: add 91 for 10-digit local numbers
            if (digits.Length == 10) digits = "91" + digits;

            return digits.Length >= 10 ? digits : trimmed;
        }
    }
}


//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Text;
//using System.Text.Json;
//using System.Text.RegularExpressions;
//using System.Threading;
//using System.Threading.Tasks;
//using Microsoft.EntityFrameworkCore;
//using Serilog;
//using xbytechat.api;
//using xbytechat.api.Features.CampaignModule.DTOs;
//using xbytechat.api.Features.CampaignModule.Models;

//namespace xbytechat.api.Features.CampaignModule.Services
//{
//    public class CsvBatchService : ICsvBatchService
//    {
//        private readonly AppDbContext _db;

//        public CsvBatchService(AppDbContext db)
//        {
//            _db = db;
//        }

//        // ----------------------------
//        // Upload + ingest
//        // ----------------------------
//        public async Task<CsvBatchUploadResultDto> CreateAndIngestAsync(
//             Guid businessId,
//             string fileName,
//             Stream stream,
//             Guid? audienceId = null,
//             CancellationToken ct = default)
//        {
//            // 1) Create batch shell
//            var batch = new CsvBatch
//            {
//                Id = Guid.NewGuid(),
//                BusinessId = businessId,
//                AudienceId = audienceId,          // nullable by design (ok if null)
//                FileName = fileName,
//                CreatedAt = DateTime.UtcNow,
//                Status = "ingesting",
//                RowCount = 0,
//                SkippedCount = 0,
//                HeadersJson = null
//            };

//            _db.CsvBatches.Add(batch);
//            await _db.SaveChangesAsync(ct);

//            try
//            {
//                // 2) Parse headers + rows (minimal robust ingest; replace with your existing parser if present)
//                //    Detect delimiter (very simple: try ',', then ';', then '\t')
//                stream.Position = 0;
//                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);

//                string? headerLine = await reader.ReadLineAsync();
//                if (string.IsNullOrWhiteSpace(headerLine))
//                {
//                    // No header line → write a single phone column as fallback so UI won’t explode
//                    var headers = new List<string> { "phone" };
//                    batch.HeadersJson = JsonSerializer.Serialize(headers);
//                    batch.Status = "ready";
//                    await _db.SaveChangesAsync(ct);

//                    Log.Warning("CSV had no header line. Created batch {BatchId} with fallback 'phone' header.", batch.Id);

//                    return new CsvBatchUploadResultDto
//                    {
//                        BatchId = batch.Id,
//                        AudienceId = batch.AudienceId,
//                        FileName = batch.FileName ?? string.Empty,
//                        RowCount = 0,
//                        Headers = headers
//                    };
//                }

//                char[] candidates = new[] { ',', ';', '\t' };
//                char delim = candidates.OrderByDescending(d => headerLine.Count(ch => ch == d)).First();

//                var headersParsed = headerLine.Split(delim).Select(h => h.Trim()).Where(h => !string.IsNullOrEmpty(h)).ToList();
//                if (headersParsed.Count == 0)
//                {
//                    headersParsed = new List<string> { "phone" };
//                }

//                batch.HeadersJson = JsonSerializer.Serialize(headersParsed);

//                // 3) Stream rows into CsvRows (first 5k for now; adapt to your chunking if needed)
//                //    If you already have a chunked/efficient parser in this class, call it instead.
//                var rowsBuffer = new List<CsvRow>(capacity: 1024);
//                int rowIndex = 0;

//                while (!reader.EndOfStream)
//                {
//                    var line = await reader.ReadLineAsync();
//                    if (line is null) break;
//                    if (string.IsNullOrWhiteSpace(line)) continue;

//                    var cols = line.Split(delim);

//                    var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
//                    for (int i = 0; i < headersParsed.Count; i++)
//                    {
//                        var key = headersParsed[i];
//                        var val = i < cols.Length ? cols[i]?.Trim() : null;
//                        dict[key] = val;
//                    }

//                    rowsBuffer.Add(new CsvRow
//                    {
//                        Id = Guid.NewGuid(),
//                        BatchId = batch.Id,
//                        RowIndex = rowIndex++,
//                        DataJson = JsonSerializer.Serialize(dict)
//                    });

//                    // bulk flush in chunks
//                    if (rowsBuffer.Count >= 1000)
//                    {
//                        _db.CsvRows.AddRange(rowsBuffer);
//                        await _db.SaveChangesAsync(ct);
//                        rowsBuffer.Clear();
//                    }
//                }

//                if (rowsBuffer.Count > 0)
//                {
//                    _db.CsvRows.AddRange(rowsBuffer);
//                    await _db.SaveChangesAsync(ct);
//                    rowsBuffer.Clear();
//                }

//                batch.RowCount = rowIndex;
//                batch.Status = "ready";
//                await _db.SaveChangesAsync(ct);

//                Log.Information("CsvBatch {BatchId} ingested: {Rows} rows; headers={HeaderCount}", batch.Id, batch.RowCount, headersParsed.Count);

//                return new CsvBatchUploadResultDto
//                {
//                    BatchId = batch.Id,
//                    AudienceId = batch.AudienceId,
//                    FileName = batch.FileName ?? string.Empty,
//                    RowCount = batch.RowCount,
//                    Headers = headersParsed
//                };
//            }
//            catch (Exception ex)
//            {
//                batch.Status = "failed";
//                batch.ErrorMessage = ex.Message;
//                await _db.SaveChangesAsync(ct);
//                Log.Error(ex, "CSV ingest failed for batch {BatchId}", batch.Id);
//                throw;
//            }
//        }

//        // ===========================================================================================

//        // Your existing methods below…
//        public async Task<IReadOnlyList<CsvRowSampleDto>> GetSamplesAsync(
//            Guid businessId,
//            Guid batchId,
//            int take = 20,
//            CancellationToken ct = default)
//        {
//            // First ensure the batch exists and belongs to the tenant
//            var batch = await _db.CsvBatches
//                .AsNoTracking()
//                .Where(b => b.Id == batchId && b.BusinessId == businessId)
//                .Select(b => new { b.Id, b.HeadersJson, b.RowCount })
//                .FirstOrDefaultAsync(ct);

//            if (batch is null)
//                throw new KeyNotFoundException("Batch not found."); // 404 semantics in your middleware

//            // If no rows yet, return empty samples gracefully (do NOT throw)
//            if (batch.RowCount <= 0)
//                return Array.Empty<CsvRowSampleDto>();

//            var headers = (string[])(JsonSerializer.Deserialize<string[]>(batch.HeadersJson ?? "[]") ?? Array.Empty<string>());

//            var rows = await _db.CsvRows
//                .AsNoTracking()
//                .Where(r => r.BatchId == batchId)
//                .OrderBy(r => r.RowIndex)
//                .Take(Math.Max(1, take))
//                .Select(r => new { r.RowIndex, r.DataJson })
//                .ToListAsync(ct);

//            var result = new List<CsvRowSampleDto>(rows.Count);
//            foreach (var r in rows)
//            {
//                var dict = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.DataJson ?? "{}")
//                           ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

//                // Ensure all header keys exist
//                foreach (var h in headers)
//                    if (!dict.ContainsKey(h)) dict[h] = null;

//                result.Add(new CsvRowSampleDto
//                {
//                    RowIndex = r.RowIndex,
//                    Data = dict
//                });
//            }

//            return result;
//        }

//        //private async Task<CsvBatchUploadResultDto> IngestCoreAsync(
//        //    Guid businessId,
//        //    string fileName,
//        //    Stream stream,
//        //    CancellationToken ct)
//        //{
//        //    // This method should match what you already implemented previously.
//        //    // Below is a minimal skeleton to indicate intent.

//        //    // 1) Create CsvBatch (BusinessId only)
//        //    var batch = new CsvBatch
//        //    {
//        //        Id = Guid.NewGuid(),
//        //        BusinessId = businessId,
//        //        FileName = fileName,
//        //        CreatedAt = DateTime.UtcNow,
//        //        HeaderJson = null, // set after parsing
//        //    };
//        //    _db.CsvBatches.Add(batch);
//        //    await _db.SaveChangesAsync(ct);

//        //    // 2) Parse & store (re-use your existing parser util)
//        //    // var (headers, rows) = await _yourParser.ParseAsync(stream, ct);
//        //    // batch.HeaderJson = JsonSerializer.Serialize(headers);
//        //    // await _db.SaveChangesAsync(ct);
//        //    // await BulkInsertRowsAsync(batch.Id, rows, ct);

//        //    // -- placeholder; call your real implementation here --
//        //    Log.Information("CsvBatch {BatchId} ingested for business {Biz}", batch.Id, businessId);

//        //    // 3) Build DTO (AudienceId intentionally null)
//        //    return new CsvBatchUploadResultDto
//        //    {
//        //        BatchId = batch.Id,
//        //        AudienceId = null,
//        //        FileName = fileName,
//        //        // Headers = headers, Sample = first few rows, etc.
//        //    };
//        //}

//        // ----------------------------
//        // Batch info
//        // ----------------------------

//        private async Task<CsvBatchUploadResultDto> IngestCoreAsync(
//            Guid businessId,
//            string fileName,
//            Stream stream,
//            CancellationToken ct)
//        {
//            // 1) Create CsvBatch (BusinessId only) — audience-agnostic by design
//            var batch = new CsvBatch
//            {
//                Id = Guid.NewGuid(),
//                BusinessId = businessId,
//                FileName = fileName,
//                CreatedAt = DateTime.UtcNow,
//                Status = "ingesting",     // optional: mark while parsing
//                HeadersJson = null,       // <-- correct property name (plural)
//                RowCount = 0,
//                SkippedCount = 0,
//                ErrorMessage = null
//            };
//            _db.CsvBatches.Add(batch);
//            await _db.SaveChangesAsync(ct);

//            // 2) Parse & store (re-use your existing parser / chunking logic)
//            //    NOTE: Keep your existing implementation that detects delimiter,
//            //    streams rows, and writes CsvRow(DataJson) in chunks.
//            //
//            // var (headers, rowsWritten, skipped) = await _yourParser.ParseAsync(batch.Id, stream, ct);
//            // batch.HeadersJson = JsonSerializer.Serialize(headers);
//            // batch.RowCount = rowsWritten;
//            // batch.SkippedCount = skipped;

//            // -- placeholder; call your real implementation above --
//            Log.Information("CsvBatch {BatchId} staged for business {Biz}", batch.Id, businessId);

//            // Mark ready once parsing completes successfully
//            batch.Status = "ready";
//            await _db.SaveChangesAsync(ct);

//            // 3) Build DTO (AudienceId intentionally null)
//            return new CsvBatchUploadResultDto
//            {
//                BatchId = batch.Id,
//                AudienceId = null,                 // by design
//                RowCount = batch.RowCount,         // or rowsWritten if you have it
//                Headers = /* headers != null ? headers.ToList() : */ new List<string>(),
//                Message = "CSV batch created."
//            };
//        }

//        public async Task<CsvBatchInfoDto?> GetBatchAsync(Guid businessId, Guid batchId, CancellationToken ct = default)
//        {
//            var batch = await _db.CsvBatches
//                .AsNoTracking()
//                .FirstOrDefaultAsync(b => b.Id == batchId && b.BusinessId == businessId, ct);

//            if (batch == null) return null;

//            var headers = SafeParseHeaderArray(batch.HeadersJson);

//            return new CsvBatchInfoDto
//            {
//                BatchId = batch.Id,
//                AudienceId = null, // see item #2 below
//                RowCount = batch.RowCount,
//                Headers = headers,
//                CreatedAt = batch.CreatedAt
//            };
//        }

//        // Kept (existing) — quick sample helper
//        public async Task<IReadOnlyList<CsvRowSampleDto>> GetSamplesAsync(Guid businessId, Guid batchId, int take = 20, CancellationToken ct = default)
//        {
//            if (take <= 0) take = 20;
//            if (take > 100) take = 100;

//            var headers = await _db.CsvBatches
//                .AsNoTracking()
//                .Where(b => b.Id == batchId && b.BusinessId == businessId)
//                .Select(b => b.HeadersJson)
//                .FirstOrDefaultAsync(ct);

//            if (headers == null) throw new KeyNotFoundException("Batch not found.");

//            var headerList = SafeParseHeaderArray(headers);

//            var rows = await _db.CsvRows
//                .AsNoTracking()
//                .Where(r => r.BatchId == batchId && r.BusinessId == businessId)
//                .OrderBy(r => r.RowIndex)
//                .Take(take)
//                .Select(r => new { r.RowIndex, r.DataJson })
//                .ToListAsync(ct);

//            var result = new List<CsvRowSampleDto>(rows.Count);
//            foreach (var r in rows)
//            {
//                var dict = SafeParseDict(r.DataJson);
//                // Ensure consistent header order in sample (fill missing with null)
//                var ordered = new Dictionary<string, string?>(headerList.Count, StringComparer.OrdinalIgnoreCase);
//                foreach (var h in headerList)
//                {
//                    dict.TryGetValue(h, out var v);
//                    ordered[h] = v;
//                }

//                result.Add(new CsvRowSampleDto
//                {
//                    RowIndex = r.RowIndex,
//                    Data = ordered
//                });
//            }

//            return result;
//        }

//        // ----------------------------
//        // List / Page / Delete / Validate
//        // ----------------------------
//        public async Task<List<CsvBatchListItemDto>> ListBatchesAsync(Guid businessId, int limit = 20, CancellationToken ct = default)
//        {
//            if (limit <= 0) limit = 20;
//            if (limit > 100) limit = 100;

//            return await _db.CsvBatches
//                .AsNoTracking()
//                .Where(b => b.BusinessId == businessId)
//                .OrderByDescending(b => b.CreatedAt)
//                .Take(limit)
//                .Select(b => new CsvBatchListItemDto
//                {
//                    BatchId = b.Id,
//                    FileName = b.FileName,
//                    RowCount = b.RowCount,
//                    Status = b.Status,
//                    CreatedAt = b.CreatedAt
//                })
//                .ToListAsync(ct);
//        }

//        public async Task<CsvBatchRowsPageDto> GetRowsPageAsync(Guid businessId, Guid batchId, int skip, int take, CancellationToken ct = default)
//        {
//            if (take <= 0) take = 50;
//            if (take > 200) take = 200;
//            if (skip < 0) skip = 0;

//            var exists = await _db.CsvBatches.AsNoTracking()
//                .AnyAsync(b => b.Id == batchId && b.BusinessId == businessId, ct);
//            if (!exists) throw new KeyNotFoundException("CSV batch not found.");

//            var total = await _db.CsvRows.AsNoTracking()
//                .Where(r => r.BusinessId == businessId && r.BatchId == batchId)
//                .CountAsync(ct);

//            var rows = await _db.CsvRows.AsNoTracking()
//                .Where(r => r.BusinessId == businessId && r.BatchId == batchId)
//                .OrderBy(r => r.RowIndex)
//                .Skip(skip)
//                .Take(take)
//                .Select(r => new CsvRowSampleDto
//                {
//                    RowIndex = r.RowIndex,
//                    Data = SafeParseDict(r.DataJson)
//                })
//                .ToListAsync(ct);

//            return new CsvBatchRowsPageDto
//            {
//                BatchId = batchId,
//                TotalRows = total,
//                Skip = skip,
//                Take = take,
//                Rows = rows
//            };
//        }

//        public async Task<bool> DeleteBatchAsync(Guid businessId, Guid batchId, CancellationToken ct = default)
//        {
//            var batch = await _db.CsvBatches
//                .FirstOrDefaultAsync(b => b.Id == batchId && b.BusinessId == businessId, ct);

//            if (batch == null) return false;

//            using var tx = await _db.Database.BeginTransactionAsync(ct);
//            try
//            {
//                var rows = _db.CsvRows.Where(r => r.BusinessId == businessId && r.BatchId == batchId);
//                _db.CsvRows.RemoveRange(rows);

//                _db.CsvBatches.Remove(batch);

//                await _db.SaveChangesAsync(ct);
//                await tx.CommitAsync(ct);
//                return true;
//            }
//            catch
//            {
//                await tx.RollbackAsync(ct);
//                throw;
//            }
//        }

//        private static readonly string[] PhoneHeaderCandidates =
//            { "phone", "mobile", "whatsapp", "msisdn", "whatsapp_number", "contact", "contact_number" };

//        public async Task<CsvBatchValidationResultDto> ValidateAsync(
//            Guid businessId,
//            Guid batchId,
//            CsvBatchValidationRequestDto request,
//            CancellationToken ct = default)
//        {
//            if (request is null) throw new ArgumentNullException(nameof(request));
//            if (request.SampleSize <= 0) request.SampleSize = 20;
//            if (request.SampleSize > 100) request.SampleSize = 100;

//            var batch = await _db.CsvBatches.AsNoTracking()
//                .Where(b => b.BusinessId == businessId && b.Id == batchId)
//                .Select(b => new { b.Id, b.HeadersJson, b.RowCount })
//                .FirstOrDefaultAsync(ct);

//            if (batch == null) throw new KeyNotFoundException("CSV batch not found.");

//            var headers = SafeParseHeaderArray(batch.HeadersJson);
//            var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);

//            var result = new CsvBatchValidationResultDto
//            {
//                BatchId = batchId,
//                TotalRows = batch.RowCount
//            };

//            // Required headers check
//            if (request.RequiredHeaders != null && request.RequiredHeaders.Count > 0)
//            {
//                foreach (var req in request.RequiredHeaders)
//                {
//                    if (!headerSet.Contains(req))
//                        result.MissingRequiredHeaders.Add(req);
//                }

//                if (result.MissingRequiredHeaders.Count > 0)
//                    result.Errors.Add("Required headers are missing.");
//            }

//            // Determine phone field
//            var phoneField = request.PhoneField;
//            if (string.IsNullOrWhiteSpace(phoneField))
//                phoneField = PhoneHeaderCandidates.FirstOrDefault(headerSet.Contains);

//            result.PhoneField = phoneField;

//            if (string.IsNullOrWhiteSpace(phoneField))
//            {
//                result.Errors.Add("No phone field provided or detected.");
//                return result; // no row scan possible without a phone column
//            }

//            // Scan rows for phone presence & duplicates
//            var seenPhones = new HashSet<string>(StringComparer.Ordinal);
//            var problemSamples = new List<CsvRowSampleDto>();

//            var rowsQuery = _db.CsvRows.AsNoTracking()
//                .Where(r => r.BusinessId == businessId && r.BatchId == batchId)
//                .OrderBy(r => r.RowIndex)
//                .Select(r => new { r.RowIndex, r.DataJson });

//            await foreach (var row in rowsQuery.AsAsyncEnumerable().WithCancellation(ct))
//            {
//                var dict = SafeParseDict(row.DataJson);
//                dict.TryGetValue(phoneField, out var rawPhone);

//                var normalized = NormalizePhoneMaybe(rawPhone, request.NormalizePhones);

//                var isProblem = false;

//                if (string.IsNullOrWhiteSpace(normalized))
//                {
//                    result.MissingPhoneCount++;
//                    isProblem = true;
//                }
//                else if (request.Deduplicate && !seenPhones.Add(normalized))
//                {
//                    result.DuplicatePhoneCount++;
//                    isProblem = true;
//                }

//                if (isProblem && problemSamples.Count < request.SampleSize)
//                {
//                    problemSamples.Add(new CsvRowSampleDto
//                    {
//                        RowIndex = row.RowIndex,
//                        Data = dict
//                    });
//                }
//            }

//            result.ProblemSamples = problemSamples;

//            if (result.MissingPhoneCount > 0)
//                result.Errors.Add("Some rows are missing phone numbers.");
//            if (result.DuplicatePhoneCount > 0)
//                result.Warnings.Add("Duplicate phone numbers detected (after normalization).");

//            return result;
//        }

//        // ----------------------------
//        // helpers
//        // ----------------------------
//        private static List<string> SafeParseHeaderArray(string? json)
//        {
//            try
//            {
//                return string.IsNullOrWhiteSpace(json)
//                    ? new List<string>()
//                    : (JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>());
//            }
//            catch { return new List<string>(); }
//        }

//        private static Dictionary<string, string?> SafeParseDict(string? json)
//        {
//            try
//            {
//                return string.IsNullOrWhiteSpace(json)
//                    ? new Dictionary<string, string?>()
//                    : (JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ??
//                       new Dictionary<string, string?>());
//            }
//            catch { return new Dictionary<string, string?>(); }
//        }

//        private static char DetectDelimiter(string headerLine)
//        {
//            var candidates = new[] { ',', ';', '\t' };
//            var counts = candidates.Select(c => (c, count: headerLine.Count(ch => ch == c))).ToList();
//            var best = counts.OrderByDescending(x => x.count).First();
//            return best.count > 0 ? best.c : ',';
//        }

//        /// <summary>
//        /// CSV parser with delimiter support: handles commas/semicolons/tabs, double quotes,
//        /// and escaped quotes (""). It does NOT support embedded newlines inside quoted fields.
//        /// </summary>
//        private static List<string> ParseCsvLine(string line, char delimiter)
//        {
//            var result = new List<string>();
//            if (line == null) return result;

//            var sb = new StringBuilder();
//            bool inQuotes = false;

//            for (int i = 0; i < line.Length; i++)
//            {
//                var c = line[i];

//                if (inQuotes)
//                {
//                    if (c == '"')
//                    {
//                        // Handle escaped quote ""
//                        if (i + 1 < line.Length && line[i + 1] == '"')
//                        {
//                            sb.Append('"');
//                            i++;
//                        }
//                        else
//                        {
//                            inQuotes = false;
//                        }
//                    }
//                    else
//                    {
//                        sb.Append(c);
//                    }
//                }
//                else
//                {
//                    if (c == delimiter)
//                    {
//                        result.Add(sb.ToString());
//                        sb.Clear();
//                    }
//                    else if (c == '"')
//                    {
//                        inQuotes = true;
//                    }
//                    else
//                    {
//                        sb.Append(c);
//                    }
//                }
//            }

//            result.Add(sb.ToString());
//            return result;
//        }

//        private static string? NormalizePhoneMaybe(string? raw, bool normalize)
//        {
//            if (string.IsNullOrWhiteSpace(raw)) return null;
//            var trimmed = raw.Trim();
//            if (!normalize) return trimmed;

//            var digits = Regex.Replace(trimmed, "[^0-9]", "");
//            digits = digits.TrimStart('0');

//            // Heuristic for India: add 91 for 10-digit local numbers
//            if (digits.Length == 10) digits = "91" + digits;

//            return digits.Length >= 10 ? digits : trimmed;
//        }
//    }
//}
