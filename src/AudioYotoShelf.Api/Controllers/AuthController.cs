using System.Security.Claims;
using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using AudioYotoShelf.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AudioYotoShelf.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    IAudiobookshelfService absService,
    IYotoService yotoService,
    AudioYotoShelfDbContext db,
    IConfiguration configuration,
    ILogger<AuthController> logger) : AppControllerBase
{
    // --- Audiobookshelf Auth ---

    /// <summary>
    /// Connect with either <see cref="Username"/> + <see cref="Password"/> or an ABS
    /// <see cref="ApiKey"/>. <see cref="BaseUrl"/> may be omitted when the server's Audiobookshelf
    /// URL is configured (<c>Audiobookshelf:Url</c>).
    /// </summary>
    public record AbsConnectRequest(
        string? BaseUrl = null, string? Username = null, string? Password = null, string? ApiKey = null);

    public record AbsConnectOptions(bool IsServerUrlLocked);

    /// <summary>Tells the setup screen whether to ask for a server URL at all.</summary>
    [AllowAnonymous]
    [HttpGet("abs/options")]
    public ActionResult<AbsConnectOptions> GetAbsConnectOptions() =>
        new AbsConnectOptions(IsServerUrlLocked: ConfiguredAbsUrl is not null);

    [AllowAnonymous]
    [HttpPost("abs/connect")]
    public async Task<IActionResult> ConnectToAudiobookshelf([FromBody] AbsConnectRequest request, CancellationToken ct)
    {
        var baseUrl = ResolveAbsUrl(request.BaseUrl);
        if (baseUrl is null)
            return BadRequest(ConfiguredAbsUrl is null
                ? "Audiobookshelf server URL is required"
                : "This app only connects to its configured Audiobookshelf server");

        var usesApiKey = !string.IsNullOrWhiteSpace(request.ApiKey);
        var loginResponse = usesApiKey
            ? await absService.AuthorizeApiKeyAsync(baseUrl, request.ApiKey!, ct)
            : await absService.LoginAsync(baseUrl, request.Username!, request.Password!, ct);
        var absUser = loginResponse.User;

        var userConnection = await db.UserConnections
            .FirstOrDefaultAsync(u => u.Username == absUser.Username, ct);

        if (userConnection is null)
        {
            userConnection = new UserConnection
            {
                Username = absUser.Username,
                AudiobookshelfUrl = baseUrl,
                DefaultLibraryId = loginResponse.UserDefaultLibraryId
            };
            db.UserConnections.Add(userConnection);
        }
        else
        {
            userConnection.AudiobookshelfUrl = baseUrl;
            userConnection.DefaultLibraryId = loginResponse.UserDefaultLibraryId ?? userConnection.DefaultLibraryId;
        }

        if (usesApiKey)
            AbsTokens.ApplyApiKey(userConnection, request.ApiKey!);
        else
            AbsTokens.ApplyLogin(userConnection, absUser);

        // Admin rights are only granted to allow-listed usernames that authenticate against the
        // TRUSTED admin Audiobookshelf server (Admin:AudiobookshelfUrl). Unless Audiobookshelf:Url is
        // configured, the URL is attacker-controllable, so admin must never be derived from a username
        // reported by an arbitrary server — requiring the trusted URL forces a real login against it.
        var adminAbsUrl = configuration["Admin:AudiobookshelfUrl"];
        var fromAdminServer = !string.IsNullOrWhiteSpace(adminAbsUrl) &&
            AudiobookshelfServer.IsSameServer(baseUrl, adminAbsUrl);
        var adminUsernames = (configuration["Admin:Usernames"] ?? configuration["ADMIN_USERNAMES"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fromAdminServer && adminUsernames.Contains(absUser.Username, StringComparer.OrdinalIgnoreCase))
            userConnection.IsAdmin = true;

        // Record the login (a session start) for usage analytics.
        userConnection.LastLoginAt = DateTimeOffset.UtcNow;
        db.LoginEvents.Add(new LoginEvent { UserConnectionId = userConnection.Id });

        await db.SaveChangesAsync(ct);

        // Only mint the admin role when the persisted flag is set AND this login is against the
        // trusted admin server, so an admin row reached via a forged BaseUrl never yields an
        // admin session.
        await IssueSessionAsync(userConnection, isAdminSession: userConnection.IsAdmin && fromAdminServer);

        logger.LogInformation("User {Username} connected to ABS at {BaseUrl} using {Method}",
            absUser.Username, baseUrl, usesApiKey ? "API key" : "password");

        return Ok(new
        {
            UserConnectionId = userConnection.Id,
            Username = absUser.Username,
            AbsConnected = true,
            YotoConnected = userConnection.HasValidYotoConnection,
            DefaultLibraryId = userConnection.DefaultLibraryId,
            Libraries = absUser.LibrariesAccessible
        });
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { LoggedOut = true });
    }

    private string? ConfiguredAbsUrl =>
        AudiobookshelfServer.ResolveConfiguredUrl(configuration[AudiobookshelfServer.UrlConfigKey]);

    /// <summary>
    /// The Audiobookshelf server a connect request may use: the configured server when set (a
    /// differing request URL resolves to null), otherwise the URL the request supplied.
    /// </summary>
    private string? ResolveAbsUrl(string? requestedUrl)
    {
        var requested = string.IsNullOrWhiteSpace(requestedUrl) ? null : requestedUrl.TrimEnd('/');
        if (ConfiguredAbsUrl is null)
            return requested;

        var matchesConfigured = requested is null || AudiobookshelfServer.IsSameServer(requested, ConfiguredAbsUrl);
        return matchesConfigured ? ConfiguredAbsUrl : null;
    }

    private async Task IssueSessionAsync(UserConnection user, bool isAdminSession)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
        };
        if (isAdminSession)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    [HttpPost("abs/validate")]
    public async Task<IActionResult> ValidateAbsToken(CancellationToken ct)
    {
        var user = await db.UserConnections.FindAsync([CurrentUserConnectionId], ct);
        if (user is null) return NotFound();

        // Renew the stored access token from the refresh token if it's expiring, then validate it.
        string token;
        try
        {
            token = await AbsTokens.EnsureValidAsync(db, absService, user, logger, ct);
        }
        catch (HttpRequestException)
        {
            // Refresh token expired/revoked — the connection is no longer valid.
            return Ok(new { Valid = false });
        }

        var isValid = await absService.ValidateTokenAsync(
            user.AudiobookshelfUrl, token, ct);

        if (isValid)
            user.AudiobookshelfTokenValidatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return Ok(new { Valid = isValid });
    }

    // --- Yoto OAuth Authorization Code Flow ---

    [HttpGet("yoto/authorize")]
    public async Task<IActionResult> AuthorizeYoto(CancellationToken ct)
    {
        var userConnectionId = CurrentUserConnectionId;
        var user = await db.UserConnections.FindAsync([userConnectionId], ct);
        if (user is null) return NotFound();

        var nonce = Guid.NewGuid().ToString("N");
        user.YotoDeviceCode = nonce; // reuse column for OAuth state nonce
        await db.SaveChangesAsync(ct);

        var state = $"{userConnectionId}:{nonce}";
        var redirectUri = $"{Request.Scheme}://{Request.Host}/api/auth/yoto/callback";
        var authUrl = yotoService.GetAuthorizationUrl(redirectUri, state);

        return Ok(new { authUrl });
    }

    // Top-level redirect back from Yoto. Identity here comes from the single-use, unguessable
    // `state` nonce we persisted on the row in AuthorizeYoto — not from the session — so it stays
    // anonymous-safe while remaining bound to the connection that initiated the flow.
    [AllowAnonymous]
    [HttpGet("yoto/callback")]
    public async Task<IActionResult> YotoCallback(
        [FromQuery] string code, [FromQuery] string state, CancellationToken ct)
    {
        // Parse state = "{userConnectionId}:{nonce}"
        var parts = state.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var userConnectionId))
            return BadRequest("Invalid state parameter");

        var nonce = parts[1];
        var user = await db.UserConnections.FindAsync([userConnectionId], ct);
        if (user is null) return NotFound();

        if (user.YotoDeviceCode != nonce)
            return BadRequest("State mismatch — possible CSRF attack");

        var redirectUri = $"{Request.Scheme}://{Request.Host}/api/auth/yoto/callback";
        var tokenResponse = await yotoService.ExchangeAuthCodeAsync(code, redirectUri, ct);

        user.YotoAccessToken = tokenResponse.AccessToken;
        user.YotoRefreshToken = tokenResponse.RefreshToken;
        user.YotoTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
        user.YotoDeviceCode = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {Username} connected to Yoto via auth code flow", user.Username);

        return Redirect("/setup");
    }

    // --- User Settings (Phase 3) ---

    [HttpPatch("settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] Core.DTOs.Transfer.UpdateSettingsRequest request,
        CancellationToken ct)
    {
        var user = await db.UserConnections.FindAsync([CurrentUserConnectionId], ct);
        if (user is null) return NotFound();

        if (request.DefaultLibraryId is not null)
            user.DefaultLibraryId = request.DefaultLibraryId;

        if (request.DefaultMinAge.HasValue)
            user.DefaultMinAge = request.DefaultMinAge.Value;

        if (request.DefaultMaxAge.HasValue)
            user.DefaultMaxAge = request.DefaultMaxAge.Value;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Settings updated for user {Username}", user.Username);

        return Ok(MapConnectionStatus(user, User.IsInRole("Admin")));
    }

    // --- Connection Status ---

    [HttpGet("status")]
    public async Task<IActionResult> GetConnectionStatus(CancellationToken ct)
    {
        var user = await db.UserConnections.FindAsync([CurrentUserConnectionId], ct);
        if (user is null) return NotFound();

        return Ok(MapConnectionStatus(user, User.IsInRole("Admin")));
    }

    // isAdmin reflects the current session's actual privilege (the cookie's Admin role), not just
    // the persisted flag — the two only differ for a session that isn't from the trusted server.
    private static object MapConnectionStatus(UserConnection user, bool isAdmin) => new
    {
        user.Id,
        user.Username,
        AbsConnected = user.HasValidAbsConnection,
        user.AudiobookshelfUrl,
        YotoConnected = user.HasValidYotoConnection,
        YotoTokenExpiresAt = user.YotoTokenExpiresAt,
        user.DefaultLibraryId,
        user.DefaultMinAge,
        user.DefaultMaxAge,
        IsAdmin = isAdmin
    };
}
