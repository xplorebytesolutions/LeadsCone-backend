using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using xbytechat.api;

namespace xbytechat.api.Features.Webhooks.Services.Resolvers
{
    public class MessageIdResolver : IMessageIdResolver
    {
        private readonly AppDbContext _context;
        private readonly ILogger<MessageIdResolver> _logger;

        public MessageIdResolver(AppDbContext context, ILogger<MessageIdResolver> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<Guid?> ResolveCampaignSendLogIdAsync(string messageId)
        {
            var log = await _context.CampaignSendLogs
                                .FirstOrDefaultAsync(l => l.MessageId == messageId);

            if (log == null)
            {
                _logger.LogWarning("⚠️ CampaignSendLog not found for MessageId: {MessageId}", messageId);
                return null;
            }

            return log.Id;
        }

        public async Task<Guid?> ResolveMessageLogIdAsync(string messageId)
        {
            var log = await _context.MessageLogs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.MessageId == messageId);

            if (log == null)
            {
                _logger.LogWarning("⚠️ MessageLog not found for MessageId: {MessageId}", messageId);
                return null;
            }

            return log.Id;
        }

        public async Task<Guid?> ResolveBusinessIdByMessageIdAsync(string messageId)
        {
            var log = await _context.MessageLogs
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.MessageId == messageId);

            if (log == null)
            {
                _logger.LogWarning("⚠️ MessageLog not found for MessageId: {MessageId}", messageId);
                return null;
            }

            return log.BusinessId;
        }

    }
}
