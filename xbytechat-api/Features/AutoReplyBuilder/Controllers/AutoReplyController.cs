using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using xbytechat.api.Features.AutoReplyBuilder.DTOs;
using xbytechat.api.Features.AutoReplyBuilder.Services;
using xbytechat.api.Shared;

namespace xbytechat.api.Features.AutoReplyBuilder.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AutoReplyController : ControllerBase
    {
        private readonly IAutoReplyService _service;

        public AutoReplyController(IAutoReplyService service)
        {
            _service = service;
        }

        [HttpPost]
        public async Task<IActionResult> CreateRule([FromBody] AutoReplyRuleDto dto)
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);
            var result = await _service.CreateRuleAsync(businessId, dto);
            return Ok(result);
        }

        [HttpGet]
        public async Task<IActionResult> GetAllRules()
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);
            var rules = await _service.GetAllRulesAsync(businessId);
            return Ok(rules);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetRuleById(Guid id)
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);
            var rule = await _service.GetRuleByIdAsync(id, businessId);
            return rule == null ? NotFound() : Ok(rule);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateRule(Guid id, [FromBody] AutoReplyRuleDto dto)
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);
            dto.Id = id;
            var success = await _service.UpdateRuleAsync(businessId, dto);
            return success ? NoContent() : NotFound();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteRule(Guid id)
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);
            var success = await _service.DeleteRuleAsync(id, businessId);
            return success ? NoContent() : NotFound();
        }

        // Optional — for debugging match logic (not exposed in prod)
        [HttpGet("match")]
        public async Task<IActionResult> MatchByKeyword([FromQuery] string message)
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);
            var matchedRule = await _service.MatchRuleByKeywordAsync(businessId, message);
            return matchedRule == null ? NotFound("No match found.") : Ok(matchedRule);
        }
    }
}
