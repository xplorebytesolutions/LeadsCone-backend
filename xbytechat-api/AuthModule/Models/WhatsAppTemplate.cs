using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace xbytechat.api.AuthModule.Models
{
    [Index(nameof(BusinessId), nameof(Provider))]
    [Index(nameof(BusinessId), nameof(Name))]
    [Index(nameof(BusinessId), nameof(Name), nameof(Language), nameof(Provider), IsUnique = true)]
    public class WhatsAppTemplate
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        public Guid BusinessId { get; set; }

        [MaxLength(40)]
        public string Provider { get; set; } = "meta_cloud";   // "meta_cloud" | "pinnacle" | etc.

        [MaxLength(120)]
        public string? ExternalId { get; set; }                // Meta template id if available

        [MaxLength(160)]
        public string Name { get; set; } = "";

        [MaxLength(16)]
        public string Language { get; set; } = "en_US";

        [MaxLength(32)]
        public string Status { get; set; } = "APPROVED";       // APPROVED/ACTIVE/REJECTED/PENDING

        [MaxLength(40)]
        public string? Category { get; set; }                  // e.g. MARKETING, UTILITY

        public string Body { get; set; } = "";

        public bool HasImageHeader { get; set; } = false;

        public int PlaceholderCount { get; set; } = 0;

        // JSON blobs (use TEXT in PG)
        public string ButtonsJson { get; set; } = "[]";        // serialized List<ButtonMetadataDto>
        public string RawJson { get; set; } = "{}";            // provider raw item (for audit/debug)

        public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;             // soft-disable if deprecated
    }
}