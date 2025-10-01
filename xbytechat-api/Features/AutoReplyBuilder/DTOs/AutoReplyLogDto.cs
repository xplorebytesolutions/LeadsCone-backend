using System;

namespace xbytechat.api.Features.AutoReplyBuilder.DTOs
{
    public class AutoReplyLogDto
    {
        public Guid Id { get; set; }
        public Guid BusinessId { get; set; }
        public Guid ContactId { get; set; }

        public string TriggerType { get; set; } = "rule"; // or "flow"
        public string TriggerKeyword { get; set; } = string.Empty;
        public string ReplyContent { get; set; } = string.Empty;

        public DateTime TriggeredAt { get; set; }

        public string? FlowName { get; set; }

        public Guid? MessageLogId { get; set; }
    }
}
