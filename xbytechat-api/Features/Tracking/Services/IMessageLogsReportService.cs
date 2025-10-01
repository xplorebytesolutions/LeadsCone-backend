// 📄 Features/Tracking/Services/IMessageLogsReportService.cs
using xbytechat.api.Shared; // PaginatedResponse<T>
using xbytechat.api.Features.Tracking.DTOs;

namespace xbytechat.api.Features.Tracking.Services
{
    /// <summary>
    /// Query interface for the universal Message Logs report.
    /// </summary>
    public interface IMessageLogsReportService
    {
        /// <summary>
        /// Searches message logs with server-side filtering, sorting, and paging.
        /// Returns a PaginatedResponse with items and counts.
        /// </summary>
        Task<PaginatedResponse<MessageLogListItemDto>> SearchAsync(
            Guid businessId,
            MessageLogReportQueryDto q,
            CancellationToken ct);
    }
}
