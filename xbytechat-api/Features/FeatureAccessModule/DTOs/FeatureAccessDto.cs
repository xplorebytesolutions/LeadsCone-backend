using System;

namespace xbytechat.api.Features.FeatureAccessModule.DTOs
{
    public class FeatureAccessDto
    {
        public Guid Id { get; set; }

        public Guid BusinessId { get; set; }

        public string FeatureName { get; set; } = string.Empty;

        public bool IsEnabled { get; set; }

        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
