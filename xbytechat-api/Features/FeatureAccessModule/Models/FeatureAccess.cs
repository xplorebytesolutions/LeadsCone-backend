using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace xbytechat.api.Features.FeatureAccessModule.Models
{
    [Table("FeatureAccess")]
    public class FeatureAccess
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid BusinessId { get; set; }

        [Required]
        [MaxLength(50)]
        public string FeatureName { get; set; } // e.g. "CRM", "Campaigns", "Catalog"
        [MaxLength(200)]
        public string Description { get; set; } = string.Empty;
        [Required]
        public bool IsEnabled { get; set; } // ✅ true = allow, false = restrict

        public string? Notes { get; set; } // Optional: reason or context

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(50)]
        public string Group { get; set; } = string.Empty;
        public string? Plan { get; set; } // e.g. "basic", "smart", "advanced"

    }
}