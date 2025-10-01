using xbytechat.api.Features.AccessControl.DTOs;
using xbytechat.api.Features.AccessControl.Models;

namespace xbytechat.api.CRM.Services
{
    public interface IPermissionService
    {
       // Task<IEnumerable<object>> GetGroupedPermissionsAsync();
        Task<IEnumerable<GroupedPermissionDto>> GetGroupedPermissionsAsync();
    }
}
