using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using xbytechat.api.Features.Webhooks.Services;
using xbytechat.api.Features.Webhooks.DTOs;

public class WebhookQueueWorker : BackgroundService
{
    private readonly IWebhookQueueService _queueService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookQueueWorker> _logger;

    public WebhookQueueWorker(
        IWebhookQueueService queueService,
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookQueueWorker> logger)
    {
        _queueService = queueService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Webhook Queue Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var payload = await _queueService.DequeueAsync(stoppingToken);
                var clonedPayload = payload.Clone(); // ✅ Avoid disposal issue

                using var scope = _scopeFactory.CreateScope();

                // 🔄 Resolve scoped dependencies
                var dispatcher = scope.ServiceProvider.GetRequiredService<IWhatsAppWebhookDispatcher>();
                var failureLogger = scope.ServiceProvider.GetRequiredService<IFailedWebhookLogService>();

                // 🚀 Dispatch
                await dispatcher.DispatchAsync(clonedPayload);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("🛑 Graceful shutdown requested.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error while processing webhook payload.");

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var failureLogger = scope.ServiceProvider.GetRequiredService<IFailedWebhookLogService>();

                    var fallback = new FailedWebhookLogDto
                    {
                        SourceModule = "WebhookQueueWorker",
                        FailureType = "DispatchError",
                        ErrorMessage = ex.Message,
                       // RawJson = ex.Data["payload"]?.ToString() ?? "(unavailable)",
                        RawJson = ex.Data["payload"]?.ToString() ?? "{}",
                        CreatedAt = DateTime.UtcNow
                    };

                    await failureLogger.LogFailureAsync(fallback);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "⚠️ Failed to log to FailedWebhookLogs table.");
                }
            }
        }

        _logger.LogInformation("🛑 Webhook Queue Worker stopped.");
    }
}
