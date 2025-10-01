using System;
using System.ComponentModel.DataAnnotations;

namespace xbytechat.api.Features.AutoReplyBuilder.Flows.Models
{
    public class AutoReplyFlow
    {
        [Key]
        public Guid Id { get; set; }

        public Guid BusinessId { get; set; }

        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string NodesJson { get; set; } = string.Empty;

        [Required]
        public string EdgesJson { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? TriggerKeyword { get; set; }

        public bool IsActive { get; set; } = true;

        public string? IndustryTag { get; set; }    // e.g., "restaurant", "clinic", "education"
        public string? UseCase { get; set; }        // e.g., "Order Flow", "Booking Flow"
        public bool IsDefaultTemplate { get; set; } = false; // Flag to indicate system-provided template
        public string? Keyword { get; set; }

    }
}

