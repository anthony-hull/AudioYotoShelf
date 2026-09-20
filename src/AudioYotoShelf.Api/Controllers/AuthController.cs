using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
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
    ISsoFlowStore ssoFlows,
    IConfiguration configuration,
    ILogger<AuthController> logger) : AppControllerBase
{
    private const string SsoCallbackPath = "/api/auth/abs/sso/callback";
    private const string SsoBrowserCookieName = "ays_sso";
    private const string SsoBrowserCookiePath = "/api/auth/abs/sso";
    private const int SsoBrowserNonceBytes = 32;
    private const string SetupPath = "/setup";
    private static readonly TimeSpan SsoFlowLifetime = TimeSpan.FromMinutes(5);

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

        var userConnection = await SignInAbsUserAsync(baseUrl, loginResponse, user =>
        {
            if (usesApiKey)
                AbsTokens.ApplyApiKey(user, request.ApiKey!);
            else
                AbsTokens.ApplyLogin(user, absUser);
        }, ct);

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

    /// <summary>
    /// Starts connecting as the person themselves through Audiobookshelf's OpenID flow: sends the
    /// browser to sign in, silently when their identity provider session already exists. Only
    /// meaningful when the Audiobookshelf server is configured — that is what pins where the tokens
    /// come from.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("abs/sso/start")]
    public async Task<IActionResult> StartAbsSso(CancellationToken ct)
    {
        if (ConfiguredAbsUrl is not { } baseUrl)
            return BadRequest("Single sign-on needs the Audiobookshelf server URL to be configured (Audiobookshelf:Url)");

        AbsSsoStart start;
        try
        {
            start = await absService.StartSsoAsync(baseUrl, ConfiguredAbsPublicUrl, SsoCallbackUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Audiobookshelf refused to start single sign-on");
            return Redirect($"{SetupPath}?sso=unavailable");
        }

        // Bind the attempt to this browser, so a link that finishes someone else's attempt cannot
        // sign the person who follows it in as somebody else.
        var browserNonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SsoBrowserNonceBytes));
        await ssoFlows.SaveAsync(
            start.State, new PendingSsoFlow(start.CodeVerifier, start.Cookies, browserNonce), SsoFlowLifetime, ct);
        Response.Cookies.Append(SsoBrowserCookieName, browserNonce, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            // Lax rather than Strict: the browser comes back from the identity provider on a
            // cross-site navigation, which Strict would leave the cookie off.
            SameSite = SameSiteMode.Lax,
            MaxAge = SsoFlowLifetime,
            Path = SsoBrowserCookiePath,
        });

        return Redirect(start.AuthorizationUrl);
    }

    /// <summary>
    /// Where the browser lands after signing in: trades the code for the person's own Audiobookshelf
    /// tokens, stores them, and signs them in. A state that is unknown, expired, already used, or
    /// from another browser sends them back to start again, since a refresh or double-click looks
    /// exactly like that and is not an error.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("abs/sso/callback")]
    public async Task<IActionResult> AbsSsoCallback(
        [FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        if (ConfiguredAbsUrl is not { } baseUrl)
            return BadRequest("Single sign-on needs the Audiobookshelf server URL to be configured (Audiobookshelf:Url)");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return BadRequest("Missing code or state");

        Request.Cookies.TryGetValue(SsoBrowserCookieName, out var browserNonce);
        var flow = await ssoFlows.TakeAsync(state, browserNonce, ct);
        Response.Cookies.Delete(SsoBrowserCookieName, new CookieOptions { Path = SsoBrowserCookiePath });
        if (flow is null)
            return Redirect($"{SetupPath}?sso=expired");

        AbsLoginResponse loginResponse;
        try
        {
            loginResponse = await absService.CompleteSsoAsync(baseUrl, code, state, flow.CodeVerifier, flow.AbsCookies, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Audiobookshelf refused the single sign-on code exchange");
            return Redirect($"{SetupPath}?sso=unavailable");
        }

        await SignInAbsUserAsync(baseUrl, loginResponse, user => AbsTokens.ApplyLogin(user, loginResponse.User), ct);

        logger.LogInformation("User {Username} connected to ABS at {BaseUrl} using single sign-on",
            loginResponse.User.Username, baseUrl);

        return Redirect(SetupPath);
    }

    /// <summary>
    /// The part of connecting every method shares: find or create the person's connection, store
    /// their credentials, work out admin rights, record the login and issue the session.
    /// </summary>
    private async Task<UserConnection> SignInAbsUserAsync(
        string baseUrl, AbsLoginResponse loginResponse, Action<UserConnection> applyCredentials, CancellationToken ct)
    {
        var absUser = loginResponse.User;
        var userConnection = await UpsertConnectionAsync(baseUrl, loginResponse, ct);
        applyCredentials(userConnection);

        // Admin rights are only granted to allow-listed usernames that authenticate against the
        // TRUSTED admin Audiobookshelf server (Admin:AudiobookshelfUrl). Unless Audiobookshelf:Url is
        // configured, the URL is attacker-controllable, so admin must never be derived from a username
        // reported by an arbitrary server — requiring the trusted URL forces a real login against it.
        var adminAbsUrl = configuration["Admin:AudiobookshelfUrl"];
        var fromAdminServer = !string.IsNullOrWhiteSpace(adminAbsUrl) &&
            AudiobookshelfServer.IsSameServer(baseUrl, adminAbsUrl);
        var adminUsernames = (configuration["Admin:Usernames"] ?? configuration["ADMIN_USERNAMES"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The allow-list is authoritative on the trusted server: being removed from it revokes admin at the next
        // trusted login. A login anywhere else leaves the stored flag alone, so a forged BaseUrl can neither
        // grant nor strip admin.
        if (fromAdminServer)
            userConnection.IsAdmin = adminUsernames.Contains(absUser.Username, StringComparer.OrdinalIgnoreCase);

        // Record the login (a session start) for usage analytics.
        userConnection.LastLoginAt = DateTimeOffset.UtcNow;
        db.LoginEvents.Add(new LoginEvent { UserConnectionId = userConnection.Id });

        await db.SaveChangesAsync(ct);

        // Only mint the admin role when the persisted flag is set AND this login is against the
        // trusted admin server, so an admin row reached via a forged BaseUrl never yields an
        // admin session.
        await IssueSessionAsync(userConnection, isAdminSession: userConnection.IsAdmin && fromAdminServer);
        return userConnection;
    }

    private async Task<UserConnection> UpsertConnectionAsync(
        string baseUrl, AbsLoginResponse loginResponse, CancellationToken ct)
    {
        var username = loginResponse.User.Username;
        var userConnection = await db.UserConnections.FirstOrDefaultAsync(u => u.Username == username, ct);

        if (userConnection is null)
        {
            userConnection = new UserConnection
            {
                Username = username,
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

        return userConnection;
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

    private string? ConfiguredAbsPublicUrl => AudiobookshelfServer.ResolveConfiguredUrl(
        configuration[AudiobookshelfServer.PublicUrlConfigKey], AudiobookshelfServer.PublicUrlConfigKey);

    // Must match the entry in Audiobookshelf's Allowed Mobile Redirect URIs character for character.
    private string SsoCallbackUrl => $"{Request.Scheme}://{Request.Host}{SsoCallbackPath}";

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

        return Redirect(SetupPath);
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
