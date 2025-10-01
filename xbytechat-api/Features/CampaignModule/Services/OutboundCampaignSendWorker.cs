using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using xbytechat.api;
using xbytechat.api.Features.CampaignModule.Services;

namespace xbytechat.api.Features.CampaignModule.Services
{
    /// <summary>
    /// Background worker that claims due jobs and invokes CampaignService to send.
    /// Flips Campaign.Status for truthful UI: Queued -> Sending -> Sent / Queued / Failed
    /// </summary>
    public class OutboundCampaignSendWorker : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<OutboundCampaignSendWorker> _log;

        // Simple global concurrency cap & polling cadence
        private const int MaxParallel = 3;
        private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(10);

        public OutboundCampaignSendWorker(IServiceProvider sp, ILogger<OutboundCampaignSendWorker> log)
        {
            _sp = sp; _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _sp.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var now = DateTimeOffset.UtcNow;

                    // Find up to MaxParallel due jobs
                    var due = await db.OutboundCampaignJobs
                        .Where(j => j.Status == "queued" && j.NextAttemptAt <= now)
                        .OrderBy(j => j.NextAttemptAt)
                        .ThenBy(j => j.CreatedAt)
                        .Take(MaxParallel)
                        .ToListAsync(stoppingToken);

                    // Claim jobs (do NOT increment Attempt here)
                    foreach (var job in due)
                    {
                        job.Status = "running";
                        job.UpdatedAt = DateTime.UtcNow;
                    }
                    if (due.Count > 0)
                        await db.SaveChangesAsync(stoppingToken);

                    var tasks = due.Select(job => ProcessJobAsync(job.Id, stoppingToken)).ToArray();
                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Send queue sweep failed");
                }

                await Task.Delay(SweepEvery, stoppingToken);
            }
        }

        private async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queue = scope.ServiceProvider.GetRequiredService<IOutboundCampaignQueueService>();
            var campaignService = scope.ServiceProvider.GetRequiredService<ICampaignService>();
            var log = scope.ServiceProvider.GetRequiredService<ILogger<OutboundCampaignSendWorker>>();

            var job = await db.OutboundCampaignJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job == null) return;

            // Mark Campaign -> Sending
            var campaign = await db.Campaigns
                .FirstOrDefaultAsync(c => c.Id == job.CampaignId && c.BusinessId == job.BusinessId, ct);

            if (campaign != null && campaign.Status != "Sending")
            {
                campaign.Status = "Sending";
                campaign.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            try
            {
                var result = await campaignService.SendTemplateCampaignWithTypeDetectionAsync(job.CampaignId);

                if (result.Success)
                {
                    if (campaign != null)
                    {
                        campaign.Status = "Sent";
                        campaign.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }

                    await queue.MarkSucceededAsync(job.Id);
                    log.LogInformation("Job {Job} succeeded for campaign {Campaign}", jobId, job.CampaignId);
                }
                else
                {
                    // Compute whether we will retry BEFORE calling MarkFailed (Attempt not yet incremented)
                    var willRetry = job.Attempt + 1 < job.MaxAttempts;

                    if (campaign != null)
                    {
                        campaign.Status = willRetry ? "Queued" : "Failed";
                        campaign.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }

                    await queue.MarkFailedAsync(job.Id, result.Message ?? "Unknown send error", scheduleRetry: true);
                    log.LogWarning("Job {Job} failed for campaign {Campaign}: {Msg}", jobId, job.CampaignId, result.Message);
                }
            }
            catch (Exception ex)
            {
                var willRetry = job.Attempt + 1 < job.MaxAttempts;

                if (campaign != null)
                {
                    campaign.Status = willRetry ? "Queued" : "Failed";
                    campaign.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                await queue.MarkFailedAsync(job.Id, ex.ToString(), scheduleRetry: true);
                log.LogWarning(ex, "Job {Job} exception for campaign {Campaign}", jobId, job.CampaignId);
            }
        }
    }
}
