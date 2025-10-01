// 📁 xbytechat.api/AuthModule/DTOs/FeatureAccessDto.cs
namespace xbytechat.api.AuthModule.DTOs
{
    public class FeatureAccessDto
    {
        public Dictionary<string, bool> Features { get; set; } = new();
    }
}
