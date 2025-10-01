﻿using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using xbytechat_api.Features.Billing.Services;

namespace xbytechat_api.Features.Billing.Controllers
{
    [ApiController]
    [Route("api/webhooks/whatsapp/meta")]
    public class WhatsAppMetaWebhookController : ControllerBase
    {
        private readonly ILogger<WhatsAppMetaWebhookController> _log;
        private readonly IBillingIngestService _ingest;
        private readonly IConfiguration _config;
        public WhatsAppMetaWebhookController(ILogger<WhatsAppMetaWebhookController> log, IBillingIngestService ingest, IConfiguration config)
        {
            _log = log;
            _ingest = ingest;
            _config = config;
        }

        // Meta verification handshake
        // GET /api/webhooks/whatsapp/meta?hub.mode=subscribe&hub.challenge=...&hub.verify_token=...&businessId=...
        [HttpGet]
        public IActionResult Verify([FromQuery(Name = "hub.mode")] string mode,
                                    [FromQuery(Name = "hub.challenge")] string challenge,
                                    [FromQuery(Name = "hub.verify_token")] string verifyToken,
                                    [FromQuery] Guid? businessId = null)
        {
            var expected = _config["WhatsApp:MetaVerifyToken"]; // optional; if empty we accept
            if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, verifyToken))
            {
                _log.LogWarning("Meta webhook verify failed. Provided token does not match.");
                return Unauthorized();
            }
            _log.LogInformation("Meta webhook verified. BusinessId={BusinessId}", businessId);
            return Content(challenge ?? string.Empty, "text/plain");
        }

        // POST /api/webhooks/whatsapp/meta?businessId=...
        [HttpPost]
        public async Task<IActionResult> Post([FromQuery] Guid businessId)
        {
            using var reader = new StreamReader(Request.Body);
            var payload = await reader.ReadToEndAsync();

            _log.LogInformation("Meta webhook payload ({Len} chars) for Biz {Biz}", payload?.Length ?? 0, businessId);
            await _ingest.IngestFromWebhookAsync(businessId, "META_CLOUD", payload);

            return Ok();
        }

        // If you need GET verification for Meta webhook, add it here.
    }
}
