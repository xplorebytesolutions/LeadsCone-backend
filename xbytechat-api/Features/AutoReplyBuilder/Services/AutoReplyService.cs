using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using xbytechat.api.Features.AutoReplyBuilder.DTOs;
using xbytechat.api.Features.AutoReplyBuilder.Models;
using xbytechat.api.Features.AutoReplyBuilder.Repositories;

namespace xbytechat.api.Features.AutoReplyBuilder.Services
{
    public class AutoReplyService : IAutoReplyService
    {
        private readonly IAutoReplyRepository _repository;

        public AutoReplyService(IAutoReplyRepository repository)
        {
            _repository = repository;
        }

        public async Task<AutoReplyRuleDto> CreateRuleAsync(Guid businessId, AutoReplyRuleDto dto)
        {
            var model = new AutoReplyRule
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                TriggerKeyword = dto.TriggerKeyword,
                ReplyMessage = dto.ReplyMessage,
                MediaUrl = dto.MediaUrl,
                Priority = dto.Priority,
                IsActive = dto.IsActive,
                CreatedAt = DateTime.UtcNow
            };

            var saved = await _repository.AddAsync(model);

            return ToDto(saved);
        }

        public async Task<IEnumerable<AutoReplyRuleDto>> GetAllRulesAsync(Guid businessId)
        {
            var rules = await _repository.GetAllByBusinessIdAsync(businessId);
            return rules.Select(ToDto);
        }

        public async Task<AutoReplyRuleDto?> GetRuleByIdAsync(Guid ruleId, Guid businessId)
        {
            var rule = await _repository.GetByIdAsync(ruleId, businessId);
            return rule == null ? null : ToDto(rule);
        }

        public async Task<bool> UpdateRuleAsync(Guid businessId, AutoReplyRuleDto dto)
        {
            var existing = await _repository.GetByIdAsync(dto.Id!.Value, businessId);
            if (existing == null) return false;

            existing.TriggerKeyword = dto.TriggerKeyword;
            existing.ReplyMessage = dto.ReplyMessage;
            existing.MediaUrl = dto.MediaUrl;
            existing.Priority = dto.Priority;
            existing.IsActive = dto.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;

            return await _repository.UpdateAsync(existing);
        }

        public async Task<bool> DeleteRuleAsync(Guid ruleId, Guid businessId)
        {
            return await _repository.DeleteAsync(ruleId, businessId);
        }

        public async Task<AutoReplyRuleDto?> MatchRuleByKeywordAsync(Guid businessId, string incomingMessage)
        {
            var rule = await _repository.MatchByKeywordAsync(businessId, incomingMessage);
            return rule == null ? null : ToDto(rule);
        }

        private AutoReplyRuleDto ToDto(AutoReplyRule rule)
        {
            return new AutoReplyRuleDto
            {
                Id = rule.Id,
                TriggerKeyword = rule.TriggerKeyword,
                ReplyMessage = rule.ReplyMessage,
                MediaUrl = rule.MediaUrl,
                Priority = rule.Priority,
                IsActive = rule.IsActive,
                CreatedAt = rule.CreatedAt,
                UpdatedAt = rule.UpdatedAt
            };
        }
    }
}
