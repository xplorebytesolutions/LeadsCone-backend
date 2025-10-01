using System;
using System.Threading.Tasks;

namespace xbytechat.api.Features.AutoReplyBuilder.Services
{
    public interface IAutoReplyRuntimeService
    {
        Task<bool> TryRunAutoReplyFlowAsync(Guid businessId, string messageText, Guid contactId, string phoneNumber);
        Task RunFlowAsync(Guid flowId, Guid businessId, Guid contactId, string phone, string keyword, string flowName);
        Task TryRunAutoReplyFlowByButtonAsync(Guid businessId, string phone, string buttonText, Guid? refMessageId = null);
        Task RunFlowFromButtonAsync(Guid flowId, Guid businessId, Guid contactId, string phone, string buttonText);

    }
}

