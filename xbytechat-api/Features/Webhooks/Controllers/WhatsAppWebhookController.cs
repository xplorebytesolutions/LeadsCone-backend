using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using xbytechat.api.Features.Tracking.DTOs;
using xbytechat.api.Features.Webhooks.Services;

namespace xbytechat.api.Features.Webhooks.Controllers
{
    [ApiController]
    [Route("api/webhooks/whatsapp")]
    public class WhatsAppWebhookController : ControllerBase
    {
        private readonly ILogger<WhatsAppWebhookController> _logger;
        private readonly IConfiguration _config;
        private readonly AppDbContext _context;
        private readonly IWhatsAppWebhookService _webhookService;
        private readonly IWebhookQueueService _queue;
            public WhatsAppWebhookController(ILogger<WhatsAppWebhookController> logger,
            IConfiguration config, AppDbContext context, IWhatsAppWebhookService webhookService, IWebhookQueueService queue)
        {
            _logger = logger;
            _config = config;
            _context = context;
            _webhookService = webhookService;
            _queue = queue;
        }

        // ✅ Step 1: Meta verification endpoint (GET)
        // Meta calls this to verify your webhook with hub.verify_token and expects you to return hub.challenge
        [HttpGet]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            // 🔐 Load your secret token from config or environment
            var expectedToken = _config["WhatsApp:MetaToken"];

            if (mode == "subscribe" && token == expectedToken)
            {
                _logger.LogInformation("✅ WhatsApp webhook verified successfully.");
                return Ok(challenge); // Meta expects a 200 OK with the challenge value
            }

            _logger.LogWarning("❌ WhatsApp webhook verification failed.");
            return Forbid("Token mismatch.");
        }

     
        [HttpPost]
        public IActionResult HandleStatus([FromBody] JsonElement payload)
        {
            _logger.LogWarning("📥 Webhook received at controller:\n" + payload.ToString());

            try
            {
                var cloned = payload.Clone(); // Important to clone here
                _queue.Enqueue(cloned);

                _logger.LogInformation("📥 Webhook payload enqueued successfully.");
                return Ok(new { received = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to enqueue WhatsApp webhook payload.");
                return StatusCode(500, new { error = "Webhook queue failed" });
            }
        }


    }
}
