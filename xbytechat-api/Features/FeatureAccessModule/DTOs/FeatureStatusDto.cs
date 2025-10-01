namespace xbytechat.api.Features.FeatureAccess.DTOs;

public class FeatureStatusDto
{
    public string FeatureCode { get; set; } = string.Empty;
    public bool IsAvailableInPlan { get; set; }
    public bool? IsOverridden { get; set; }
}
