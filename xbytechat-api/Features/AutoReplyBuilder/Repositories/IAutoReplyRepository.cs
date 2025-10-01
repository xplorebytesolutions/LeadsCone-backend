using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xbytechat.api.Features.AutoReplyBuilder.Models;

namespace xbytechat.api.Features.AutoReplyBuilder.Repositories
{
    public interface IAutoReplyRepository
    {
        Task<AutoReplyRule> AddAsync(AutoReplyRule rule);
        Task<IEnumerable<AutoReplyRule>> GetAllByBusinessIdAsync(Guid businessId);
        Task<AutoReplyRule?> GetByIdAsync(Guid ruleId, Guid businessId);
        Task<bool> UpdateAsync(AutoReplyRule rule);
        Task<bool> DeleteAsync(Guid ruleId, Guid businessId);

        // Runtime keyword match logic
        Task<AutoReplyRule?> MatchByKeywordAsync(Guid businessId, string incomingMessage);
        Task<bool> LinkFlowToRuleAsync(Guid businessId, string keyword, Guid flowId, string? flowName);
        Task<AutoReplyRule> UpsertRuleLinkedToFlowAsync(Guid businessId, string keyword, Guid flowId, string? flowName);

    }
}
