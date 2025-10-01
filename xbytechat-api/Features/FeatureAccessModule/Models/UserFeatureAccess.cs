using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace xbytechat.api.Features.FeatureAccessModule.Models
{
    [Table("UserFeatureAccess")]
    public class UserFeatureAccess
    {
        [Key]
        public Guid Id { get; set; }
        public Guid BusinessId { get; set; }
        [Required]
        public Guid UserId { get; set; }

        [Required]
        [MaxLength(50)]
        public string FeatureName { get; set; } = string.Empty;

        [Required]
        public bool IsEnabled { get; set; }

        public string? Notes { get; set; }

        public Guid? ModifiedByUserId { get; set; }  // Who applied this override
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
