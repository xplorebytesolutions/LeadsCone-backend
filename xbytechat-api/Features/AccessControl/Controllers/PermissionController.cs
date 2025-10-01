using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security;
using xbytechat.api.CRM.Services;
using xbytechat.api.Features.AccessControl.DTOs;
using xbytechat.api.Helpers;

namespace xbytechat.api.Features.AccessControl.Controllers
{

    [ApiController]
    [Route("api/permission")]
    [Authorize]
    public class PermissionController : Controller
    {

        private readonly IPermissionService _permissionService;
        public PermissionController(IPermissionService permissionService)
        {
            _permissionService = permissionService;    
        }
        [HttpGet("Grouped")]
        public async Task<IActionResult> GetGroupedPermissions()
        {
            var grouped = await _permissionService.GetGroupedPermissionsAsync();
            return Ok(ResponseResult.SuccessInfo("Permissions grouped by category", grouped));
        }

       
    }
}
