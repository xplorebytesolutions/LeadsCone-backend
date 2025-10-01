using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using xbytechat.api.Features.AutoReplyBuilder.Models;

namespace xbytechat.api.Features.AutoReplyBuilder.Repositories
{
    public class AutoReplyRepository : IAutoReplyRepository
    {
        private readonly AppDbContext _dbContext;

        public AutoReplyRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<AutoReplyRule> AddAsync(AutoReplyRule rule)
        {
            _dbContext.AutoReplyRules.Add(rule);
            await _dbContext.SaveChangesAsync();
            return rule;
        }

        public async Task<IEnumerable<AutoReplyRule>> GetAllByBusinessIdAsync(Guid businessId)
        {
            return await _dbContext.AutoReplyRules
                .Where(r => r.BusinessId == businessId && r.IsActive)
                .OrderBy(r => r.Priority)
                .ToListAsync();
        }

        public async Task<AutoReplyRule?> GetByIdAsync(Guid ruleId, Guid businessId)
        {
            return await _dbContext.AutoReplyRules
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.BusinessId == businessId);
        }

        public async Task<bool> UpdateAsync(AutoReplyRule rule)
        {
            _dbContext.AutoReplyRules.Update(rule);
            return await _dbContext.SaveChangesAsync() > 0;
        }

        public async Task<bool> DeleteAsync(Guid ruleId, Guid businessId)
        {
            var rule = await GetByIdAsync(ruleId, businessId);
            if (rule == null) return false;

            _dbContext.AutoReplyRules.Remove(rule);
            return await _dbContext.SaveChangesAsync() > 0;
        }

        public async Task<AutoReplyRule?> MatchByKeywordAsync(Guid businessId, string incomingMessage)
        {
            return await _dbContext.AutoReplyRules
                .Where(r => r.BusinessId == businessId && r.IsActive)
                .OrderBy(r => r.Priority)
                .FirstOrDefaultAsync(r => incomingMessage.Contains(r.TriggerKeyword));
        }

        public async Task<bool> LinkFlowToRuleAsync(Guid businessId, string keyword, Guid flowId, string? flowName)
        {
            var rule = await _dbContext.AutoReplyRules
                .FirstOrDefaultAsync(r => r.BusinessId == businessId && r.TriggerKeyword.ToLower() == keyword.ToLower());

            if (rule == null) return false;

            rule.FlowId = flowId;
            rule.FlowName = flowName ?? "";
            rule.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
            return true;
        }
        public async Task<AutoReplyRule> UpsertRuleLinkedToFlowAsync(Guid businessId, string keyword, Guid flowId, string? flowName)
        {
            // Ensure keyword is normalized
            var normalizedKeyword = keyword.ToLower().Trim();

            var rule = await _dbContext.AutoReplyRules
                .FirstOrDefaultAsync(r => r.BusinessId == businessId && r.TriggerKeyword.ToLower() == normalizedKeyword);

            if (rule != null)
            {
                // Update existing rule
                rule.FlowId = flowId;
                rule.FlowName = flowName ?? "";
                rule.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                // Create new rule
                rule = new AutoReplyRule
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    TriggerKeyword = normalizedKeyword,
                    FlowId = flowId,
                    FlowName = flowName ?? "",
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true,
                    Priority = 1,
                    ReplyMessage = "" // Fallback (optional)
                };

                _dbContext.AutoReplyRules.Add(rule);
            }

            await _dbContext.SaveChangesAsync();
            return rule;
        }

    }
}
