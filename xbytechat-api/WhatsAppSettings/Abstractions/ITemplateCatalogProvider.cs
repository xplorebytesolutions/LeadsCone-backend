using xbytechat_api.WhatsAppSettings.Models;

namespace xbytechat.api.WhatsAppSettings.Abstractions
{
    public interface ITemplateCatalogProvider
    {
        Task<IReadOnlyList<TemplateCatalogItem>> ListAsync(WhatsAppSettingEntity setting, CancellationToken ct = default);
        Task<TemplateCatalogItem?> GetByNameAsync(WhatsAppSettingEntity setting, string templateName, CancellationToken ct = default);
    }
}
