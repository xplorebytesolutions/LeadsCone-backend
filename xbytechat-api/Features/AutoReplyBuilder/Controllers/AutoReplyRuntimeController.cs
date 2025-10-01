using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using xbytechat.api.Features.AutoReplyBuilder.DTOs;
using xbytechat.api.Features.AutoReplyBuilder.Services;
using xbytechat.api.Shared;

namespace xbytechat.api.Features.AutoReplyBuilder.Controllers
{
    [ApiController]
    [Route("api/auto-reply-runtime")]
    [Authorize]
    public class AutoReplyRuntimeController : ControllerBase
    {
        private readonly IAutoReplyRuntimeService _runtimeService;
        private readonly ILogger<AutoReplyRuntimeController> _logger;

        public AutoReplyRuntimeController(
            IAutoReplyRuntimeService runtimeService,
            ILogger<AutoReplyRuntimeController> logger)
        {
            _runtimeService = runtimeService;
            _logger = logger;
        }

        // 🔁 Runtime button reply based on keyword (used in message click)
        [HttpPost("button-click")]
        public async Task<IActionResult> HandleButtonClick([FromBody] AutoReplyButtonClickDto dto)
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);

            _logger.LogInformation("🔘 Button clicked: BusinessId={BusinessId}, Phone={Phone}, Button={ButtonText}, RefMsg={RefMessageId}",
                businessId, dto.Phone, dto.ButtonText, dto.RefMessageId?.ToString() ?? "null");

            await _runtimeService.TryRunAutoReplyFlowByButtonAsync(
                businessId,
                dto.Phone,
                dto.ButtonText,
                dto.RefMessageId
            );

            return Ok(new { success = true });
        }

        // 🧪 Manual test (canvas-based flow trigger)
        [HttpPost("flow-by-button")]
        public async Task<IActionResult> TriggerFlowByButton([FromBody] AutoReplyButtonClickDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Phone) || string.IsNullOrWhiteSpace(dto.ButtonText))
                return BadRequest("Phone and ButtonText are required.");

            try
            {
                _logger.LogInformation("🚀 Triggering flow from button: FlowId={FlowId}, BusinessId={BusinessId}, ContactId={ContactId}, Phone={Phone}, ButtonText={ButtonText}",
                    dto.FlowId, dto.BusinessId, dto.ContactId, dto.Phone, dto.ButtonText);

                await _runtimeService.RunFlowFromButtonAsync(
                    dto.FlowId,
                    dto.BusinessId,
                    dto.ContactId,
                    dto.Phone,
                    dto.ButtonText.Trim()
                );

                return Ok(new
                {
                    success = true,
                    flowId = dto.FlowId,
                    contactId = dto.ContactId,
                    triggeredAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to trigger flow from button click: FlowId={FlowId}, Phone={Phone}, Button={ButtonText}",
                    dto.FlowId, dto.Phone, dto.ButtonText);

                return StatusCode(500, "Internal server error");
            }
        }
    }
}
