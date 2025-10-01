using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Models;

namespace xbytechat.api.Features.AutoReplyBuilder.Models
{
    public class AutoReplyRule
    {
        [Key]
        public Guid Id { get; set; }

        public Guid BusinessId { get; set; }

        public string TriggerKeyword { get; set; } = string.Empty;

        public string ReplyMessage { get; set; } = string.Empty;

        public string? MediaUrl { get; set; }

        public int Priority { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public string? FlowName { get; set; }

        // ✅ NEW: Link to the flow
        public Guid? FlowId { get; set; }

        [ForeignKey("FlowId")]
        public AutoReplyFlow? Flow { get; set; }

        public string? IndustryTag { get; set; } // e.g., "restaurant", "clinic", "real_estate"
        public string? SourceChannel { get; set; } // e.g., "whatsapp", "instagram"
    }
}
