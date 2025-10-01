using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using xbytechat.api.Features.AutoReplyBuilder.DTOs;
using xbytechat.api.Features.AutoReplyBuilder.Flows.DTOs;
using xbytechat.api.Features.AutoReplyBuilder.Flows.Services;
using xbytechat.api.Shared;

namespace xbytechat.api.Features.AutoReplyBuilder.Flows.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AutoReplyFlowsController : ControllerBase
    {
        private readonly IAutoReplyFlowService _service;

        public AutoReplyFlowsController(IAutoReplyFlowService service)
        {
            _service = service;
        }

        // [HttpPost("save")]
        //public async Task<IActionResult> SaveFlow([FromBody] SaveFlowDto dto)
        //{
        //    var id = await _service.SaveFlowAsync(dto);
        //    return Ok(new { id });
        //}
        [HttpPost("save")]
        public async Task<IActionResult> SaveFlow([FromBody] SaveFlowDto dto)
        {
            Guid businessId;
            try { businessId = User.GetBusinessId(); }
            catch (UnauthorizedAccessException) { return Unauthorized("Missing or invalid business ID"); }

            var id = await _service.SaveFlowAsync(dto, businessId);
            return Ok(new { id });
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetFlowsByBusiness(Guid businessId)
        {
            var flows = await _service.GetFlowsByBusinessIdAsync(businessId);
            return Ok(flows);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetFlowById(Guid id)
        {
            var businessId = ClaimsBusinessDetails.GetBusinessId(User);
            var flow = await _service.GetFlowByIdAsync(id, businessId);
            return flow == null ? NotFound() : Ok(flow);
        }
        [HttpGet("business/{businessId}/count")]
        public async Task<IActionResult> GetFlowCount(Guid businessId)
        {
            var count = await _service.GetFlowCountForBusinessAsync(businessId);
            return Ok(count);
        }
        [HttpPut("{id}/rename")]
        public async Task<IActionResult> RenameFlow(Guid id, [FromBody] RenameFlowDto dto)
        {
            var result = await _service.RenameFlowAsync(id, dto.NewName);
            if (!result) return NotFound();
            return Ok();
        }
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteFlow(Guid id)
        {
            Guid businessId;
            try
            {
                businessId = User.GetBusinessId(); // ✅ Clean and secure
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized("Missing or invalid business ID");
            }

            var success = await _service.DeleteFlowAsync(id, businessId);
            if (!success)
                return NotFound("Flow not found or not owned by your business");

            return Ok(new { message = "Flow deleted successfully" });
        }


    }
}

