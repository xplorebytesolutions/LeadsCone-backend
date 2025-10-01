using System;

namespace xbytechat.api.Features.AutoReplyBuilder.DTOs
{
    /// <summary>
    /// DTO used to create or retrieve AutoReplyRule.
    /// </summary>
    public class AutoReplyRuleDto
    {
        public Guid? Id { get; set; } // Nullable to allow re-use for Create and Update
        public string TriggerKeyword { get; set; } = string.Empty;
        public string ReplyMessage { get; set; } = string.Empty;
        public string? MediaUrl { get; set; } // Optional media
        public int Priority { get; set; } = 0; // Lower = higher priority
        public bool IsActive { get; set; } = true;

        // Audit Fields (optional for now, useful for admin UI)
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
