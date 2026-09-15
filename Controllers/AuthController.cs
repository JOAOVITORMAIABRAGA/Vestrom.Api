using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vestrom.Api.Models;
using Vestrom.Api.Services;

namespace Vestrom.Api.Controllers;

[ApiController, Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [AllowAnonymous, HttpPost("login")]
    public async Task<ActionResult<Session>> Login(LoginRequest request, CancellationToken ct)
    {
        var session = await auth.LoginAsync(request.Username, request.Password, ct);
        return session is null ? Unauthorized(new { detail = "Invalid username or password." }) : Ok(session);
    }

    [Authorize, HttpGet("me")]
    public async Task<ActionResult<AuthUser>> Me(CancellationToken ct)
    {
        var user = await auth.GetUserAsync(User, ct); return user is null ? Unauthorized() : Ok(user);
    }

    [Authorize, HttpPost("logout")]
    public IActionResult Logout() => NoContent();
}
