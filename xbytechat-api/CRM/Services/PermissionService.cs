using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using xbytechat.api.CRM.Services;
using xbytechat.api.Features.AccessControl.DTOs;
using xbytechat.api.Features.AccessControl.Models;

namespace xbytechat.api.Features.AccessControl.Services
{
    public class PermissionService : IPermissionService
    {
        private readonly AppDbContext _context;

        public PermissionService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<GroupedPermissionDto>> GetGroupedPermissionsAsync()
        {
            return await _context.Permissions
                .Where(p => p.IsActive)
                .GroupBy(p => p.Group ?? "Ungrouped")
                .Select(g => new GroupedPermissionDto
                {
                    Group = g.Key,
                    Features = g.ToList()
                })
                .ToListAsync();
        }
       


    }
}