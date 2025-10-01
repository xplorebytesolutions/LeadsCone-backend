using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using xbytechat.api.AuthModule.Models;
using xbytechat.api.Features.BusinessModule.Services;
using xbytechat.api.Features.CampaignModule.DTOs;
using xbytechat.api.Features.CampaignModule.Models;
using xbytechat.api.Features.CampaignModule.Services;
using xbytechat.api.Features.MessagesEngine.Services;
using xbytechat.api.Helpers;
using xbytechat.api.Shared;
using static xbytechat.api.Features.MessagesEngine.Controllers.MessageEngineController;

namespace xbytechat.api.Features.CampaignModule.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CampaignController : ControllerBase
    {
        private readonly ICampaignService _campaignService;
        private readonly IBusinessService _businessService;
        private readonly IMessageEngineService _messageService;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CampaignController(
            ICampaignService campaignService,
            IBusinessService businessService,
            IMessageEngineService messageEngineService,
            IHttpContextAccessor httpContextAccessor)
        {
            _campaignService = campaignService;
            _businessService = businessService;
            _messageService = messageEngineService;
            _httpContextAccessor = httpContextAccessor;
        }


        [HttpGet("get-image-campaign")]
        public async Task<IActionResult> GetAll([FromQuery] string? type)
        {
            var businessId = GetBusinessIdOrThrow();
            var items = await _campaignService.GetAllCampaignsAsync(businessId, type);
            return Ok(items);
        }
        [HttpGet("paginated")]
        public async Task<IActionResult> GetPaginatedCampaigns([FromQuery] PaginatedRequest request)
        {
            var user = HttpContext.User;
            var businessIdClaim = user.FindFirst("businessId");

            if (businessIdClaim == null || !Guid.TryParse(businessIdClaim.Value, out var businessId))
                return Unauthorized(new { message = "🚫 Invalid or missing BusinessId claim." });

            var result = await _campaignService.GetPaginatedCampaignsAsync(businessId, request);
            return Ok(result);
        }

        [HttpGet("debug-claims")]
        public IActionResult DebugClaims()
        {
            var user = HttpContext.User;
            var businessId = user.FindFirst("businessId")?.Value;

            return Ok(new
            {
                name = user.Identity?.Name,
                businessId
            });
        }

        [HttpPost("create-text-campaign")]
        public async Task<IActionResult> CreateTextCampaign([FromBody] CampaignCreateDto dto)
        {
            try
            {
                var businessIdClaim = User.FindFirst("businessId")?.Value;
                if (!Guid.TryParse(businessIdClaim, out var businessId))
                    return Unauthorized(new { message = "🚫 Invalid or missing BusinessId claim." });

                var createdBy = User.Identity?.Name ?? "system";

                if (string.IsNullOrWhiteSpace(dto.Name))
                    return BadRequest(new { message = "🚫 Campaign name is required." });

                if (string.IsNullOrWhiteSpace(dto.TemplateId))
                    return BadRequest(new { message = "🚫 TemplateId is required for template campaigns." });

                if (string.IsNullOrWhiteSpace(dto.MessageTemplate))
                    return BadRequest(new { message = "🚫 Message template content is required." });

                var campaignId = await _campaignService.CreateTextCampaignAsync(dto, businessId, createdBy);

                return campaignId != null
                    ? Ok(new
                    {
                        success = true,
                        message = "✅ Campaign created successfully",
                        campaignId = campaignId.Value
                    })
                    : BadRequest(new { success = false, message = "❌ Failed to create campaign" });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Exception in CreateTextCampaign");
                return StatusCode(500, new { message = "🚨 Internal server error", error = ex.Message });
            }
        }

        [HttpPost("create-image-campaign")]
        public async Task<IActionResult> CreateImageCampaign([FromBody] CampaignCreateDto dto)
        {
            try
            {
                var user = HttpContext.User;
                var businessIdClaim = user.FindFirst("businessId");

                if (businessIdClaim == null || !Guid.TryParse(businessIdClaim.Value, out var businessId))
                    return Unauthorized(new { message = "🚫 Invalid or missing BusinessId claim." });

                if (dto.MultiButtons != null && dto.MultiButtons.Any())
                {
                    var allowedTypes = new[] { "url", "copy_code", "flow", "phone_number", "quick_reply" };
                    foreach (var button in dto.MultiButtons)
                    {
                        var type = button.ButtonType?.Trim().ToLower();

                        if (!allowedTypes.Contains(type))
                            return BadRequest(new { message = $"❌ Invalid ButtonType: '{type}' is not supported." });

                        var needsValue = new[] { "url", "flow", "copy_code", "phone_number" };
                        if (needsValue.Contains(type) && string.IsNullOrWhiteSpace(button.TargetUrl))
                            return BadRequest(new { message = $"❌ Button '{button.ButtonText}' requires a valid TargetUrl or Value for type '{type}'." });

                        if (button.TargetUrl?.ToLower() == "unknown")
                            return BadRequest(new { message = $"❌ Invalid value 'unknown' found in button '{button.ButtonText}'." });
                    }
                }

                var createdBy = user.Identity?.Name ?? "system";
                var campaignId = await _campaignService.CreateImageCampaignAsync(businessId, dto, createdBy);

                return Ok(new
                {
                    success = true,
                    message = "✅ Campaign created successfully",
                    campaignId
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Exception in CreateImageCampaign");
                return StatusCode(500, new { message = "🚨 Internal server error", error = ex.Message });
            }
        }

        // ✅ Moved above {id} routes
        [HttpPost("{id}/assign-contacts")]
        public async Task<IActionResult> AssignContactsToCampaign(Guid id, [FromBody] AssignContactsDto request)
        {
            try
            {
                var businessId = GetBusinessId();
                var success = await _campaignService.AssignContactsToCampaignAsync(id, businessId, request.ContactIds);

                return success
                    ? Ok(new { message = "✅ Contacts assigned" })
                    : BadRequest(new { message = "❌ Failed to assign contacts" });
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Error assigning contacts: " + ex.Message);
                return StatusCode(500, new { message = "Internal error", error = ex.Message });
            }
        }

        [HttpDelete("{campaignId}/recipients/{contactId}")]
        public async Task<IActionResult> RemoveCampaignRecipient(Guid campaignId, Guid contactId)
        {
            try
            {
                var businessId = GetBusinessId();
                var success = await _campaignService.RemoveRecipientAsync(businessId, campaignId, contactId);

                if (!success)
                    return NotFound(new { message = "Recipient not found or not assigned" });

                return NoContent();
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Remove recipient failed: " + ex.Message);
                return StatusCode(500, new { message = "Error removing recipient", detail = ex.Message });
            }
        }

        // Put this inside CampaignService (same class as SendTemplateCampaignWithTypeDetectionAsync)
        private static string? ResolveRecipientPhone(CampaignRecipient r)
        {
            // Try Contact first, then AudienceMember fallbacks
            return r?.Contact?.PhoneNumber
                ?? r?.AudienceMember?.PhoneE164
                ?? r?.AudienceMember?.PhoneRaw;
        }

        // Send All Type of campaign method 
        [HttpPost("send-campaign/{campaignId}")] // use to send free text and Template campaigns
        public async Task<IActionResult> SendTemplateCampaign(Guid campaignId)
        {
            try
            {
                var result = await _campaignService.SendTemplateCampaignWithTypeDetectionAsync(campaignId);
                return result.Success ? Ok(result) : BadRequest(result);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Exception while sending image template campaign");
                return StatusCode(500, ResponseResult.ErrorInfo("🚨 Server error while sending campaign", ex.ToString()));
            }
        }

        [HttpPost("send-template-campaign/{id}")]
        public async Task<IActionResult> SendImageCampaign(Guid id)
        {
            var result = await _campaignService.SendTemplateCampaignAsync(id);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpPost("send/{campaignId}")]
        public async Task<IActionResult> SendCampaign(Guid campaignId)
        {
            try
            {
                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var userAgent = Request.Headers["User-Agent"].ToString() ?? "unknown";

                var success = await _campaignService.SendCampaignAsync(campaignId, ipAddress, userAgent);

                return success
                    ? Ok(new { success = true, message = "✅ Campaign sent successfully" })
                    : BadRequest(new { success = false, message = "❌ Campaign sending failed" });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Exception in SendCampaign");
                return StatusCode(500, new { success = false, message = "🚨 Internal Server Error", error = ex.Message });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateCampaign(Guid id, [FromBody] CampaignCreateDto dto)
        {
            var result = await _campaignService.UpdateCampaignAsync(id, dto);
            return result
                ? Ok(new { message = "✏️ Campaign updated successfully" })
                : BadRequest(new { message = "❌ Update failed — only draft campaigns can be edited" });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteCampaign(Guid id)
        {
            var result = await _campaignService.DeleteCampaignAsync(id);
            return result
                ? Ok(new { message = "🗑️ Campaign deleted successfully" })
                : BadRequest(new { message = "❌ Delete failed — only draft campaigns can be deleted" });
        }
        private Guid GetBusinessIdOrThrow()
        {
            string? raw =
                User?.FindFirst("business_id")?.Value ??
                User?.FindFirst("BusinessId")?.Value ??
                User?.FindFirst("businessId")?.Value ??
                Request.Headers["X-Business-Id"].FirstOrDefault();

            if (!Guid.TryParse(raw, out var id))
                throw new UnauthorizedAccessException("Business context missing.");
            return id;
        }
        [HttpGet("recipients/{id}")]
        public async Task<IActionResult> GetCampaignRecipients(Guid id)
        {
            try
            {
                var businessId = GetBusinessId();
                var recipients = await _campaignService.GetRecipientsByCampaignIdAsync(id, businessId);
                return Ok(recipients);
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Error fetching campaign recipients: " + ex.Message);
                return StatusCode(500, new { message = "Error fetching recipients", detail = ex.Message });
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<CampaignDto>> GetCampaignById(Guid id)
        {
            var businessId = GetBusinessId();
            var campaign = await _campaignService.GetCampaignByIdAsync(id, businessId);

            if (campaign == null)
                return NotFound();

            return Ok(campaign);
        }

        private Guid GetBusinessId()
        {
            var claim = HttpContext.User.FindFirst("businessId")?.Value;
            if (string.IsNullOrEmpty(claim))
                throw new UnauthorizedAccessException("BusinessId not found in token claims.");

            return Guid.Parse(claim);
        }
    
        [HttpGet("list/{businessId:guid}")]
        public async Task<IActionResult> GetAvailableFlows(Guid businessId, [FromQuery] bool onlyPublished = true)
        {
            var items = await _campaignService.GetAvailableFlowsAsync(businessId, onlyPublished);
            return Ok(new { success = true, items });
        }


    }
}
