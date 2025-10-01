using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using xbytechat.api.Features.CampaignModule.DTOs;     // OutboundCampaignJobDto
using xbytechat.api.Features.CampaignModule.Models;
using xbytechat.api.Features.CampaignModule.Services; // IOutboundCampaignQueueService
using xbytechat.api.Features.Queueing.DTOs;           // OutboundCampaignJobCreateDto

namespace xbytechat.api.Features.Queueing.Services
{
    /// <summary>
    /// No-op queue adapter so the dispatcher compiles & runs. Swap for a real queue later.
    /// </summary>
    public sealed class NoopOutboundCampaignQueueService : IOutboundCampaignQueueService
    {
        public Task<Guid> EnqueueAsync(Guid businessId, Guid campaignId, bool forceDuplicate = false)
        {
            var id = Guid.NewGuid();
            Log.Information("Noop queue: EnqueueAsync business={BusinessId} campaign={CampaignId} forceDup={Force} -> {JobId}",
                businessId, campaignId, forceDuplicate, id);
            return Task.FromResult(id);
        }

        public Task<bool> MarkSucceededAsync(Guid jobId)
        {
            Log.Information("Noop queue: MarkSucceededAsync job={JobId}", jobId);
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(Guid jobId, string error, bool scheduleRetry = true)
        {
            Log.Warning("Noop queue: MarkFailedAsync job={JobId} retry={Retry} error={Error}",
                jobId, scheduleRetry, error);
            return Task.FromResult(true);
        }

        public Task<List<OutboundCampaignJobDto>> GetJobsForCampaignAsync(Guid businessId, Guid campaignId)
        {
            Log.Information("Noop queue: GetJobsForCampaignAsync business={BusinessId} campaign={CampaignId}",
                businessId, campaignId);
            return Task.FromResult(new List<OutboundCampaignJobDto>());
        }

        public Task<OutboundCampaignJobDto?> GetActiveJobForCampaignAsync(Guid businessId, Guid campaignId)
        {
            Log.Information("Noop queue: GetActiveJobForCampaignAsync business={BusinessId} campaign={CampaignId}",
                businessId, campaignId);
            return Task.FromResult<OutboundCampaignJobDto?>(null);
        }

        public Task<bool> CancelAsync(Guid businessId, Guid jobId)
        {
            Log.Information("Noop queue: CancelAsync business={BusinessId} job={JobId}", businessId, jobId);
            return Task.FromResult(true);
        }

        public Task<bool> ForceRetryNowAsync(Guid businessId, Guid jobId)
        {
            Log.Information("Noop queue: ForceRetryNowAsync business={BusinessId} job={JobId}", businessId, jobId);
            return Task.FromResult(true);
        }

        public Task<int> EnqueueBulkAsync(IEnumerable<OutboundCampaignJobCreateDto> jobs, CancellationToken ct = default)
        {
            var list = jobs?.ToList() ?? new List<OutboundCampaignJobCreateDto>();
            Log.Information("Noop queue: EnqueueBulkAsync received {Count} jobs", list.Count);
            return Task.FromResult(list.Count);
        }

        Task<List<OutboundCampaignJob>> IOutboundCampaignQueueService.GetJobsForCampaignAsync(Guid businessId, Guid campaignId)
        {
            throw new NotImplementedException();
        }

        Task<OutboundCampaignJob?> IOutboundCampaignQueueService.GetActiveJobForCampaignAsync(Guid businessId, Guid campaignId)
        {
            throw new NotImplementedException();
        }
    }
}
