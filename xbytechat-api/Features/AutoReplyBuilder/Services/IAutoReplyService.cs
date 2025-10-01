using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xbytechat.api.Features.AutoReplyBuilder.DTOs;

namespace xbytechat.api.Features.AutoReplyBuilder.Services
{
    public interface IAutoReplyService
    {
        Task<AutoReplyRuleDto> CreateRuleAsync(Guid businessId, AutoReplyRuleDto dto);
        Task<IEnumerable<AutoReplyRuleDto>> GetAllRulesAsync(Guid businessId);
        Task<AutoReplyRuleDto?> GetRuleByIdAsync(Guid ruleId, Guid businessId);
        Task<bool> UpdateRuleAsync(Guid businessId, AutoReplyRuleDto dto);
        Task<bool> DeleteRuleAsync(Guid ruleId, Guid businessId);

        // For runtime matching
        Task<AutoReplyRuleDto?> MatchRuleByKeywordAsync(Guid businessId, string incomingMessage);
    }
}
