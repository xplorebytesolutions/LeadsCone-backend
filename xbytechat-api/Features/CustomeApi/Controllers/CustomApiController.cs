using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using xbytechat.api.Features.CustomeApi.Auth;
using xbytechat.api.Features.CustomeApi.DTOs;
using xbytechat.api.Features.CustomeApi.Services;

namespace xbytechat.api.Features.CustomeApi.Controllers
{
    [ApiController]
    [Route("api/custom")]
    public sealed class CustomApiController : ControllerBase
    {
        private readonly ICustomApiService _service;
        private readonly StaticApiKeyOptions _api;
        private readonly CtaJourneyPublisher _journeyPublisher;
        private readonly ILogger<CustomApiController> _logger;

        public CustomApiController(
            ICustomApiService service,
            IOptions<StaticApiKeyOptions> api,
            CtaJourneyPublisher journeyPublisher,
            ILogger<CustomApiController> logger)
        {
            _service = service;
            _api = api.Value;
            _journeyPublisher = journeyPublisher;
            _logger = logger;
        }

        /// <summary>
        /// Sends a WhatsApp template (optionally with VIDEO header) by phoneNumberId.
        /// Body: { phoneNumberId, to, templateId, variables:{ "1":"..." }, videoUrl, flowConfigId }
        /// </summary>
        [HttpPost("sendflow")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(401)]
        public async Task<IActionResult> SendTemplate([FromBody] DirectTemplateSendRequest req, CancellationToken ct = default)
        {
            if (!ModelState.IsValid)
                return BadRequest(new { success = false, message = "❌ Invalid request body.", errors = ModelState });

            var provided = ReadProvidedApiKey(Request);

            if (string.IsNullOrWhiteSpace(_api.Key) ||
                string.IsNullOrWhiteSpace(provided) ||
                !string.Equals(provided.Trim(), _api.Key.Trim(), StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Custom API auth failed. hasConfiguredKey={HasConfiguredKey}, hasXAuth={HasXAuth}, hasAuthorization={HasAuthorization}, hasQueryKey={HasQueryKey}",
                    !string.IsNullOrWhiteSpace(_api.Key),
                    !string.IsNullOrWhiteSpace(Request.Headers["X-Auth-Key"].FirstOrDefault()),
                    !string.IsNullOrWhiteSpace(Request.Headers["Authorization"].FirstOrDefault()),
                    !string.IsNullOrWhiteSpace(Request.Query["X-Auth-Key"].FirstOrDefault()) ||
                    !string.IsNullOrWhiteSpace(Request.Query["x-auth-key"].FirstOrDefault()));

                return Unauthorized(new { success = false, message = "🔒 Invalid or missing key." });
            }

            var result = await _service.SendTemplateAsync(req, ct);
            return result.Success
                ? Ok(result)
                : StatusCode(result.Code > 0 ? result.Code : StatusCodes.Status400BadRequest, result);
        }

        [HttpPost("test-webhook")]
        public async Task<IActionResult> TestWebhook([FromQuery] Guid businessId, CancellationToken ct)
        {
            var (ok, msg) = await _journeyPublisher.ValidateAndPingAsync(businessId, ct);
            return ok ? Ok(new { ok, message = msg }) : BadRequest(new { ok, message = msg });
        }

        private static string? ReadProvidedApiKey(HttpRequest request)
        {
            var xAuth = request.Headers["X-Auth-Key"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(xAuth))
                return xAuth.Trim();

            var authorization = request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                const string bearerPrefix = "Bearer ";
                if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                    return authorization[bearerPrefix.Length..].Trim();

                return authorization.Trim();
            }

            var queryKey = request.Query["X-Auth-Key"].FirstOrDefault()
                           ?? request.Query["x-auth-key"].FirstOrDefault();

            return string.IsNullOrWhiteSpace(queryKey) ? null : queryKey.Trim();
        }
    }
}
