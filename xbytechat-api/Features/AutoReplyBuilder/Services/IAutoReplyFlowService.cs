using System.Threading.Tasks;
using xbytechat.api.Features.AutoReplyBuilder.Flows.DTOs;

namespace xbytechat.api.Features.AutoReplyBuilder.Flows.Services
{
    public interface IAutoReplyFlowService
    {
        Task<Guid> SaveFlowAsync(SaveFlowDto dto, Guid businessId);
        Task<List<SaveFlowDto>> GetFlowsByBusinessIdAsync(Guid businessId);
        Task<SaveFlowDto?> GetFlowByIdAsync(Guid flowId, Guid businessId);
        Task<int> GetFlowCountForBusinessAsync(Guid businessId);
        Task<bool> RenameFlowAsync(Guid id, string newName);
        Task<bool> DeleteFlowAsync(Guid id, Guid businessId);
        Task ExecuteFlowAsync(Guid businessId, string triggerKeyword, string customerPhoneNumber);

    }

}
