using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using xbytechat.api.Models;

namespace xbytechat.api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MessageLogsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public MessageLogsController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var logs = await _db.MessageLogs
                .OrderByDescending(log => log.CreatedAt)
                .ToListAsync();

            return Ok(logs);
        }
    }
}
