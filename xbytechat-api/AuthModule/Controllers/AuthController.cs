using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using xbytechat.api.AuthModule.DTOs;
using xbytechat.api.AuthModule.Services;
using xbytechat.api.Features.BusinessModule.DTOs;

namespace xbytechat.api.AuthModule.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        // ✅ Login → return { token } (NO cookies)
        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] UserLoginDto dto)
        {
            var result = await _authService.LoginAsync(dto);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Token))
                return Unauthorized(new { success = false, message = result.Message });

            return Ok(new { token = result.Token });
        }

        // (Optional) Refresh token endpoint if you still issue refresh tokens.
        // Returns tokens in body (NO cookies).
        [AllowAnonymous]
        [HttpPost("refresh-token")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
        {
            var result = await _authService.RefreshTokenAsync(request.RefreshToken);
            if (!result.Success) return Unauthorized(new { success = false, message = result.Message });

            dynamic data = result.Data!;
            return Ok(new
            {
                accessToken = data.accessToken,
                refreshToken = data.refreshToken
            });
        }
        // ✅ Signup
        [HttpPost("business-user-signup")]
        public async Task<IActionResult> Signup([FromBody] SignupBusinessDto dto)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList();

                return BadRequest(new
                {
                    success = false,
                    message = "❌ Validation failed.",
                    errors
                });
            }

            var result = await _authService.SignupAsync(dto);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        // ✅ Logout (stateless JWT): nothing server-side to do
        [Authorize]
        [HttpPost("logout")]
        public IActionResult Logout() => Ok(new { success = true, message = "Logged out" });

        // ✅ (Optional) lightweight session echo from claims (works with Bearer)
        [Authorize]
        [HttpGet("session")]
        public IActionResult GetSession()
        {
            var user = HttpContext.User;
            if (user?.Identity is not { IsAuthenticated: true }) return BadRequest("Invalid session");

            var email = user.FindFirst(ClaimTypes.Email)?.Value ?? "unknown";
            var role = user.FindFirst(ClaimTypes.Role)?.Value
                       ?? user.FindFirst("role")?.Value
                       ?? "unknown";
            var plan = user.FindFirst("plan")?.Value ?? "basic";
            var biz = user.FindFirst("businessId")?.Value;

            return Ok(new { isAuthenticated = true, role, email, plan, businessId = biz });
        }

        [Authorize]
        [HttpGet("features")]
        public async Task<IActionResult> GetFeatureAccess()
        {
            var result = await _authService.GetFeatureAccessForUserAsync(User);
            return Ok(result.Features);
        }
    }
}


