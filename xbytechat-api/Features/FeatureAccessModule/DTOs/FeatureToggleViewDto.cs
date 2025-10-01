public class FeatureToggleViewDto
{
    public string FeatureCode { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IsAvailableInPlan { get; set; }     // From PlanManager
    public bool? IsOverridden { get; set; }         // null if no override
    public bool IsActive => IsOverridden ?? IsAvailableInPlan;
}
