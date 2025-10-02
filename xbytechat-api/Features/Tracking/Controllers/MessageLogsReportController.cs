using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using xbytechat.api.Features.Tracking.DTOs;
using xbytechat.api.Features.Tracking.Services;
using xbytechat.api.Shared; // for User.GetBusinessId()

namespace xbytechat.api.Features.Tracking.Controllers
{
    [ApiController]
    [Route("api/report/message-logs")]
    [Authorize]
    public sealed class MessageLogsReportController : ControllerBase
    {
        private readonly IMessageLogsReportService _service;

        public MessageLogsReportController(IMessageLogsReportService service)
            => _service = service;

        /// <summary>
        /// Universal message log search with server-side filtering/sorting/paging.
        /// </summary>
        [HttpPost("search")]
        [ProducesResponseType(typeof(PaginatedResponse<MessageLogListItemDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Search([FromBody] MessageLogReportQueryDto q, CancellationToken ct)
        {
            var businessId = User.GetBusinessId();
            if (businessId == Guid.Empty) return Unauthorized();

            var result = await _service.SearchAsync(businessId, q, ct);
            return Ok(result);
        }

        /// <summary>
        /// Export full result set as CSV (streams all pages).
        /// Columns match the UI export.
        /// </summary>
        [HttpPost("export/csv")]
        [Produces("text/csv")]
        public async Task<IActionResult> ExportCsv([FromBody] MessageLogReportQueryDto q, CancellationToken ct)
        {
            var businessId = User.GetBusinessId();
            if (businessId == Guid.Empty) return Unauthorized();

            // We’ll walk pages to get the full dataset (service clamps PageSize to <= 200).
            var sb = new StringBuilder();

            // Header
            sb.AppendLine(string.Join(",", new[]
            {
                "Time","Recipient","SenderId","Channel","Status","Type","Campaign",
                "Body","ProviderId","DeliveredAt","ReadAt","Error"
            }.Select(EscapeCsv)));

            var page = 1;
            const int maxLoops = 10000; // safety
            var totalWritten = 0;

            while (page <= maxLoops)
            {
                var pageQuery = new MessageLogReportQueryDto
                {
                    FromUtc = q.FromUtc,
                    ToUtc = q.ToUtc,
                    Search = q.Search,
                    Statuses = q.Statuses,
                    Channels = q.Channels,
                    SenderIds = q.SenderIds,
                    MessageTypes = q.MessageTypes,
                    CampaignId = q.CampaignId,
                    SortBy = q.SortBy,
                    SortDir = q.SortDir,
                    Page = page,
                    PageSize = 200 // max page supported by service
                };

                var res = await _service.SearchAsync(businessId, pageQuery, ct);
                if (res.Items.Count == 0) break;

                foreach (var r in res.Items)
                {
                    var time = r.SentAt ?? r.CreatedAt;
                    var row = new[]
                    {
                        EscapeCsv(time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        EscapeCsv(r.RecipientNumber ?? ""),
                        EscapeCsv(r.SenderId ?? ""),
                        EscapeCsv(r.SourceChannel ?? ""),
                        EscapeCsv(r.Status ?? ""),
                        EscapeCsv(r.MessageType ?? ""),
                        EscapeCsv(r.CampaignName ?? r.CampaignId?.ToString() ?? ""),
                        EscapeCsv((r.MessageContent ?? "").ReplaceLineEndings(" ")),
                        EscapeCsv(r.ProviderMessageId ?? ""),
                        EscapeCsv(r.DeliveredAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                        EscapeCsv(r.ReadAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                        EscapeCsv((r.ErrorMessage ?? "").ReplaceLineEndings(" "))
                    };
                    sb.AppendLine(string.Join(",", row));
                }

                totalWritten += res.Items.Count;

                // stop when we’ve written everything
                var total = res.TotalCount;
                if (totalWritten >= total) break;

                page++;
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var fileName =
                $"MessageLogs{(q.CampaignId.HasValue ? "-" + q.CampaignId.Value : "")}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";

            return File(bytes, "text/csv", fileName);

            static string EscapeCsv(string s)
            {
                // Quote if contains comma, quote, or newline
                if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
                {
                    return $"\"{s.Replace("\"", "\"\"")}\"";
                }
                return s;
            }
        }

        /// <summary>
        /// Export XLSX (stub). Implement with ClosedXML or EPPlus if desired.
        /// </summary>
        [HttpPost("export/xlsx")]
        public IActionResult ExportXlsxStub()
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new ProblemDetails
            {
                Title = "XLSX export is not enabled",
                Detail = "Implement in MessageLogsReportController.ExportXlsx using a spreadsheet library."
            });
        }

        /// <summary>
        /// Export PDF (stub). Implement with QuestPDF or iText if desired.
        /// </summary>
        [HttpPost("export/pdf")]
        public IActionResult ExportPdfStub()
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new ProblemDetails
            {
                Title = "PDF export is not enabled",
                Detail = "Implement in MessageLogsReportController.ExportPdf using a PDF library."
            });
        }
        [HttpGet("facets")]
        [ProducesResponseType(typeof(MessageLogFacetsDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Facets([FromQuery] int fromDays = 90, CancellationToken ct = default)
        {
            var businessId = User.GetBusinessId();
            if (businessId == Guid.Empty) return Unauthorized();

            var fromUtc = DateTime.UtcNow.AddDays(-Math.Abs(fromDays));
            var facets = await _service.GetFacetsAsync(businessId, fromUtc, ct);
            return Ok(facets);
        }
    }
}
