using System.Text.Json;
using System.Threading.Channels;

namespace xbytechat.api.Features.Webhooks.Services
{
    public class WebhookQueueService : IWebhookQueueService
    {
        private readonly Channel<JsonElement> _queue;

        public WebhookQueueService()
        {
            var options = new BoundedChannelOptions(5000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            };

            _queue = Channel.CreateBounded<JsonElement>(options);
        }

        public void Enqueue(JsonElement item)
        {
            if (!_queue.Writer.TryWrite(item))
            {
                throw new InvalidOperationException("⚠️ Webhook queue is full.");
            }
        }

        public async ValueTask<JsonElement> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }

        public int GetQueueLength() => _queue.Reader.Count;
    }
}
