namespace xbytechat.api.Features.FeatureAccessModule.DTOs
{
    public class UserFeatureAccessDto
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Role { get; set; } = "";
        public List<FeaturePermissionDto> Permissions { get; set; } = new();
    }

    public class FeaturePermissionDto
    {
        public string FeatureName { get; set; } = "";
        public bool IsEnabled { get; set; }
    }
}