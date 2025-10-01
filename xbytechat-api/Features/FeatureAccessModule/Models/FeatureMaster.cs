using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace xbytechat.api.Features.FeatureAccessModule.Models
{
    [Table("FeatureMaster")]
    public class FeatureMaster
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string FeatureCode { get; set; } = string.Empty; // e.g. "crm", "catalog"

        [Required]
        [MaxLength(100)]
        public string DisplayName { get; set; } = string.Empty; // e.g. "CRM", "Catalog"

        [MaxLength(250)]
        public string Description { get; set; } = string.Empty;

        [MaxLength(50)]
        public string Group { get; set; } = string.Empty; // Optional grouping like "CRM", "Messaging"

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
