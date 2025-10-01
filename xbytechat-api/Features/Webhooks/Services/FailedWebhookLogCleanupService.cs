using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace xbytechat.api.Features.Webhooks.Services
{
    public class FailedWebhookLogCleanupService : BackgroundService
    {
        private readonly ILogger<FailedWebhookLogCleanupService> _logger;
        private readonly IServiceProvider _services;
        private readonly TimeSpan _interval = TimeSpan.FromHours(24); // daily run

        public FailedWebhookLogCleanupService(ILogger<FailedWebhookLogCleanupService> logger, IServiceProvider services)
        {
            _logger = logger;
            _services = services;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🧹 FailedWebhookLogCleanupService started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var cutoff = DateTime.UtcNow.AddDays(-7);
                    var oldLogs = await db.FailedWebhookLogs
                        .Where(x => x.CreatedAt < cutoff)
                        .ToListAsync(stoppingToken);

                    if (oldLogs.Any())
                    {
                        db.FailedWebhookLogs.RemoveRange(oldLogs);
                        await db.SaveChangesAsync(stoppingToken);
                        _logger.LogInformation("🧹 Deleted {Count} old failed webhook logs.", oldLogs.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to clean up old webhook logs.");
                }

                await Task.Delay(_interval, stoppingToken); // wait before next cleanup
            }

            _logger.LogInformation("🛑 FailedWebhookLogCleanupService stopped.");
        }
    }
}
