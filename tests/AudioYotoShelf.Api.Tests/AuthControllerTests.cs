using System.Security.Claims;
using System.Text.Json;
using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class AuthControllerTests : IDisposable
{
    private readonly DbContextOptions<AudioYotoShelfDbContext> _options;
    private readonly AudioYotoShelfDbContext _db;
    private readonly AuthController _sut;
    private readonly Mock<ISsoFlowStore> _flows = new();
    private readonly Mock<IAuthenticationService> _authentication = new();

    public AuthControllerTests()
    {
        _options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"AuthCtrlTest_{Guid.NewGuid()}")
            .Options;
        _db = new AudioYotoShelfDbContext(_options);

        _sut = new AuthController(
            Mock.Of<IAudiobookshelfService>(),
            Mock.Of<IYotoService>(),
            _db,
            _flows.Object,
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<AuthController>>());
    }

    private AuthController CreateSut(
        IAudiobookshelfService absService, string? configuredAbsUrl,
        IDictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?> { ["Audiobookshelf:Url"] = configuredAbsUrl };
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
            settings[key] = value;

        var sut = new AuthController(
            absService,
            Mock.Of<IYotoService>(),
            _db,
            _flows.Object,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            Mock.Of<ILogger<AuthController>>());

        var services = new ServiceCollection().AddSingleton(_authentication.Object).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("yoto.example");
        sut.ControllerContext = new ControllerContext { HttpContext = http };
        return sut;
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // =========================================================================
    // ConnectToAudiobookshelf — which server a connection may target
    // =========================================================================

    [Fact]
    public async Task Connect_UrlDiffersFromConfiguredServer_RejectedWithoutContactingIt()
    {
        // With the server URL configured, a visitor must not be able to make this app send
        // requests (or credentials) to an address of their choosing.
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, configuredAbsUrl: "http://abs.home");

        var result = await sut.ConnectToAudiobookshelf(
            new AuthController.AbsConnectRequest("http://attacker.example", ApiKey: "key"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Connect_NoUrlAndNoConfiguredServer_Rejected()
    {
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, configuredAbsUrl: null);

        var result = await sut.ConnectToAudiobookshelf(
            new AuthController.AbsConnectRequest(null, "user", "pass"), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // =========================================================================
    // SSO connect — Start: send the person to Audiobookshelf's sign-in
    // =========================================================================

    private const string AbsUrl = "http://audiobookshelf:80";
    private const string PublicAbsUrl = "https://audiobooks.example";
    private const string SsoCookieName = "ays_sso";

    private static readonly IReadOnlyDictionary<string, string> AbsCookies =
        new Dictionary<string, string> { ["connect.sid"] = "s%3Aabc", ["auth_method"] = "openid-mobile" };

    private static Dictionary<string, string?> PublicUrlSetting =>
        new() { ["Audiobookshelf:PublicUrl"] = PublicAbsUrl };

    private static AbsSsoStart SsoStart() =>
        new("https://auth.example/authorize?state=st-1", AbsCookies, "st-1", "verifier-1");

    [Fact]
    public async Task SsoStart_NoConfiguredServer_RejectedWithoutContactingAnyone()
    {
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, configuredAbsUrl: null);

        var result = await sut.StartAbsSso(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SsoStart_AsksAbsForTheFlowUsingOurPublicCallback()
    {
        var absService = new Mock<IAudiobookshelfService>();
        absService.Setup(a => a.StartSsoAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SsoStart());
        var sut = CreateSut(absService.Object, AbsUrl, PublicUrlSetting);

        await sut.StartAbsSso(CancellationToken.None);

        absService.Verify(a => a.StartSsoAsync(
            AbsUrl, PublicAbsUrl, "https://yoto.example/api/auth/abs/sso/callback", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task SsoStart_RedirectsTheBrowserToTheAuthorizationUrl()
    {
        var absService = new Mock<IAudiobookshelfService>();
        absService.Setup(a => a.StartSsoAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SsoStart());
        var sut = CreateSut(absService.Object, AbsUrl);

        var result = await sut.StartAbsSso(CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("https://auth.example/authorize?state=st-1");
    }

    [Fact]
    public async Task SsoStart_BindsTheFlowToThisBrowserWithAnHttpOnlyCookie()
    {
        PendingSsoFlow? saved = null;
        _flows.Setup(f => f.SaveAsync("st-1", It.IsAny<PendingSsoFlow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, PendingSsoFlow, TimeSpan, CancellationToken>((_, flow, _, _) => saved = flow);
        var absService = new Mock<IAudiobookshelfService>();
        absService.Setup(a => a.StartSsoAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SsoStart());
        var sut = CreateSut(absService.Object, AbsUrl);

        await sut.StartAbsSso(CancellationToken.None);

        var setCookie = sut.Response.Headers.SetCookie.ToString();
        saved.Should().NotBeNull();
        saved!.CodeVerifier.Should().Be("verifier-1");
        saved.AbsCookies.Should().Equal(AbsCookies);
        setCookie.Should().Contain($"{SsoCookieName}={Uri.EscapeDataString(saved.BrowserNonce)}");
        setCookie.Should().Contain("httponly").And.Contain("secure");
        // Lax, not Strict: the browser arrives back from Authentik on a cross-site navigation.
        setCookie.Should().Contain("samesite=lax");
    }

    [Fact]
    public async Task SsoStart_AbsRefuses_SendsThePersonBackToSetupWithAReason()
    {
        var absService = new Mock<IAudiobookshelfService>();
        absService.Setup(a => a.StartSsoAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Invalid redirect_uri"));
        var sut = CreateSut(absService.Object, AbsUrl);

        var result = await sut.StartAbsSso(CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup?sso=unavailable");
        _flows.Verify(f => f.SaveAsync(It.IsAny<string>(), It.IsAny<PendingSsoFlow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // SSO connect — Callback: trade the code for the person's own tokens
    // =========================================================================

    private static AbsLoginResponse SsoLogin(string username = "alice") => new(
        new AbsUser("abs-u1", username, "user", "legacy", true, null, ["lib-1"],
            AccessToken: "access-jwt", RefreshToken: "refresh-token"),
        "lib-1");

    private void FlowExistsFor(string nonce = "browser-nonce") =>
        _flows.Setup(f => f.TakeAsync("st-1", nonce, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PendingSsoFlow("verifier-1", AbsCookies, nonce));

    private static void ArriveWithSsoCookie(AuthController sut, string nonce = "browser-nonce") =>
        sut.Request.Headers.Cookie = $"{SsoCookieName}={Uri.EscapeDataString(nonce)}";

    private Mock<IAudiobookshelfService> AbsThatCompletesSso(AbsLoginResponse login)
    {
        var absService = new Mock<IAudiobookshelfService>();
        absService.Setup(a => a.CompleteSsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(login);
        return absService;
    }

    [Theory]
    [InlineData(null, "st-1")]
    [InlineData("", "st-1")]
    [InlineData("code-1", null)]
    [InlineData("code-1", "")]
    public async Task SsoCallback_MissingCodeOrState_Rejected(string? code, string? state)
    {
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, AbsUrl);

        var result = await sut.AbsSsoCallback(code, state, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SsoCallback_UnknownOrExpiredState_SendsThePersonToStartAgain_WithoutCallingAbs()
    {
        // Also what a double-click or a refresh looks like: the state was already used.
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, AbsUrl);
        ArriveWithSsoCookie(sut);

        var result = await sut.AbsSsoCallback("code-1", "st-unknown", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup?sso=expired");
    }

    [Fact]
    public async Task SsoCallback_OffersTheStoreTheBrowsersCookieSoAnotherBrowserCannotFinishTheFlow()
    {
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, AbsUrl);
        ArriveWithSsoCookie(sut, "victim-browser");

        await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        _flows.Verify(f => f.TakeAsync("st-1", "victim-browser", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SsoCallback_NoCookieFromThisBrowser_TreatedAsExpired()
    {
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, AbsUrl);

        var result = await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup?sso=expired");
    }

    [Fact]
    public async Task SsoCallback_Success_StoresThePersonsOwnTokens()
    {
        FlowExistsFor();
        var absService = AbsThatCompletesSso(SsoLogin());
        var sut = CreateSut(absService.Object, AbsUrl);
        ArriveWithSsoCookie(sut);

        await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        var stored = await _db.UserConnections.SingleAsync(u => u.Username == "alice");
        stored.AudiobookshelfToken.Should().Be("access-jwt");
        stored.AudiobookshelfRefreshToken.Should().Be("refresh-token");
        stored.AudiobookshelfUrl.Should().Be(AbsUrl);
        stored.DefaultLibraryId.Should().Be("lib-1");
    }

    [Fact]
    public async Task SsoCallback_Success_HandsAbsTheCodeVerifierAndCookiesFromTheStartOfTheFlow()
    {
        FlowExistsFor();
        var absService = AbsThatCompletesSso(SsoLogin());
        var sut = CreateSut(absService.Object, AbsUrl);
        ArriveWithSsoCookie(sut);

        await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        absService.Verify(a => a.CompleteSsoAsync(AbsUrl, "code-1", "st-1", "verifier-1", AbsCookies, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task SsoCallback_Success_IssuesASessionAndReturnsToSetup()
    {
        FlowExistsFor();
        var sut = CreateSut(AbsThatCompletesSso(SsoLogin()).Object, AbsUrl);
        ArriveWithSsoCookie(sut);

        var result = await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup");
        _authentication.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(),
            It.Is<ClaimsPrincipal>(p => p.Identity!.Name == "alice"), It.IsAny<AuthenticationProperties>()));
    }

    [Fact]
    public async Task SsoCallback_Success_RemovesTheBrowserBindingCookie()
    {
        FlowExistsFor();
        var sut = CreateSut(AbsThatCompletesSso(SsoLogin()).Object, AbsUrl);
        ArriveWithSsoCookie(sut);

        await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        sut.Response.Headers.SetCookie.ToString().Should().Contain($"{SsoCookieName}=;");
    }

    [Fact]
    public async Task SsoCallback_ReconnectingAnExistingPerson_UpdatesTheirRowRatherThanAddingASecond()
    {
        _db.UserConnections.Add(TestData.CreateUserConnection(username: "alice"));
        await _db.SaveChangesAsync();
        FlowExistsFor();
        var sut = CreateSut(AbsThatCompletesSso(SsoLogin()).Object, AbsUrl);
        ArriveWithSsoCookie(sut);

        await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        (await _db.UserConnections.CountAsync(u => u.Username == "alice")).Should().Be(1);
    }

    [Fact]
    public async Task SsoCallback_AbsRejectsTheCode_DoesNotStoreAHalfConnection()
    {
        FlowExistsFor();
        var absService = new Mock<IAudiobookshelfService>();
        absService.Setup(a => a.CompleteSsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("No session"));
        var sut = CreateSut(absService.Object, AbsUrl);
        ArriveWithSsoCookie(sut);

        var result = await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup?sso=unavailable");
        (await _db.UserConnections.CountAsync()).Should().Be(0);
        _authentication.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties>()), Times.Never);
    }

    [Theory]
    [InlineData(AbsUrl, true)]
    [InlineData("http://some-other-abs:80", false)]
    public async Task SsoCallback_AdminRightsOnlyFromTheTrustedAdminServer(string adminAbsUrl, bool expectedAdmin)
    {
        FlowExistsFor();
        var settings = new Dictionary<string, string?>
        {
            ["Admin:AudiobookshelfUrl"] = adminAbsUrl,
            ["Admin:Usernames"] = "alice",
        };
        var sut = CreateSut(AbsThatCompletesSso(SsoLogin()).Object, AbsUrl, settings);
        ArriveWithSsoCookie(sut);

        await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        (await _db.UserConnections.SingleAsync()).IsAdmin.Should().Be(expectedAdmin);
        _authentication.Verify(a => a.SignInAsync(
            It.IsAny<HttpContext>(), It.IsAny<string>(),
            It.Is<ClaimsPrincipal>(p => p.IsInRole("Admin") == expectedAdmin), It.IsAny<AuthenticationProperties>()));
    }

    [Fact]
    public async Task SsoCallback_NoConfiguredServer_Rejected()
    {
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var sut = CreateSut(absService.Object, configuredAbsUrl: null);

        var result = await sut.AbsSsoCallback("code-1", "st-1", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // =========================================================================
    // GetAbsConnectOptions
    // =========================================================================

    [Theory]
    [InlineData("http://abs.home", true)]
    [InlineData(null, false)]
    public void ConnectOptions_ReportWhetherServerUrlIsLocked(string? configuredAbsUrl, bool expectedLocked)
    {
        var sut = CreateSut(Mock.Of<IAudiobookshelfService>(), configuredAbsUrl);

        var result = sut.GetAbsConnectOptions();

        result.Value!.IsServerUrlLocked.Should().Be(expectedLocked);
    }

    // =========================================================================
    // UpdateSettings
    // =========================================================================

    [Fact]
    public async Task UpdateSettings_SavesValues()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var request = new UpdateSettingsRequest(
            DefaultLibraryId: "lib-new",
            DefaultMinAge: 3,
            DefaultMaxAge: 8);

        var result = await _sut.AsUser(user.Id).UpdateSettings(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var updated = await _db.UserConnections.FindAsync(user.Id);
        updated!.DefaultLibraryId.Should().Be("lib-new");
        updated.DefaultMinAge.Should().Be(3);
        updated.DefaultMaxAge.Should().Be(8);
    }

    [Fact]
    public async Task UpdateSettings_PartialUpdate_KeepsExisting()
    {
        var user = TestData.CreateUserConnection();
        user.DefaultMinAge = 2;
        user.DefaultMaxAge = 12;
        user.DefaultLibraryId = "lib-original";
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        // Only update max age
        var request = new UpdateSettingsRequest(DefaultMaxAge: 15);

        await _sut.AsUser(user.Id).UpdateSettings(request, CancellationToken.None);

        var updated = await _db.UserConnections.FindAsync(user.Id);
        updated!.DefaultMinAge.Should().Be(2); // Unchanged
        updated.DefaultMaxAge.Should().Be(15); // Updated
        updated.DefaultLibraryId.Should().Be("lib-original"); // Unchanged
    }

    [Fact]
    public async Task UpdateSettings_NotFound_Returns404()
    {
        var request = new UpdateSettingsRequest(DefaultMinAge: 5);

        var result = await _sut.AsUser(Guid.NewGuid()).UpdateSettings(request, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // =========================================================================
    // GetConnectionStatus
    // =========================================================================

    [Fact]
    public async Task GetStatus_ReturnsUserStatus()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).GetConnectionStatus(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetStatus_NotFound_Returns404()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).GetConnectionStatus(CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // =========================================================================
    // Mutation-testing additions — the connect flow, admin grants, sessions and token/OAuth state
    // =========================================================================

    private const string AbsUrl = "http://abs.home";
    private const string ApiKey = "key-123";

    /// <summary>Everything a connect test needs to see: the controller, what was signed in, and the ABS mock.</summary>
    private sealed class ConnectRig(
        AuthController controller, Mock<IAuthenticationService> authentication, Mock<IAudiobookshelfService> abs)
    {
        public AuthController Controller { get; } = controller;
        public Mock<IAudiobookshelfService> Abs { get; } = abs;
        public ClaimsPrincipal? SignedIn { get; private set; }
        public string? SignedInScheme { get; private set; }

        public void Capture(string? scheme, ClaimsPrincipal principal)
        {
            SignedInScheme = scheme;
            SignedIn = principal;
        }

        public Mock<IAuthenticationService> Authentication { get; } = authentication;
    }

    private ConnectRig CreateRig(
        Dictionary<string, string?> settings, IYotoService? yoto = null, ClaimsPrincipal? user = null)
    {
        var abs = new Mock<IAudiobookshelfService>(MockBehavior.Strict);
        var authentication = new Mock<IAuthenticationService>();
        var services = new ServiceCollection().AddSingleton(authentication.Object).BuildServiceProvider();
        var controller = new AuthController(
            abs.Object,
            yoto ?? Mock.Of<IYotoService>(),
            _db,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            Mock.Of<ILogger<AuthController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services, User = user ?? new ClaimsPrincipal() },
            },
        };
        var rig = new ConnectRig(controller, authentication, abs);
        authentication
            .Setup(a => a.SignInAsync(It.IsAny<HttpContext>(), It.IsAny<string?>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<AuthenticationProperties?>()))
            .Callback<HttpContext, string?, ClaimsPrincipal, AuthenticationProperties?>((_, scheme, principal, _) => rig.Capture(scheme, principal))
            .Returns(Task.CompletedTask);
        return rig;
    }

    private static Dictionary<string, string?> Settings(
        string? configuredUrl = AbsUrl, string? adminUrl = null, string? adminUsers = null, string? envAdminUsers = null) =>
        new()
        {
            ["Audiobookshelf:Url"] = configuredUrl,
            ["Admin:AudiobookshelfUrl"] = adminUrl,
            ["Admin:Usernames"] = adminUsers,
            ["ADMIN_USERNAMES"] = envAdminUsers,
        };

    private static AbsLoginResponse LoginResponse(
        string username = "alice", string? defaultLibraryId = "lib-1", string token = "legacy-token", string? refreshToken = null) =>
        new(new AbsUser("abs-1", username, "user", token, true, null, ["lib-1", "lib-2"], RefreshToken: refreshToken), defaultLibraryId);

    private static void ExpectApiKeyLogin(ConnectRig rig, string expectedBaseUrl, AbsLoginResponse response) =>
        rig.Abs.Setup(s => s.AuthorizeApiKeyAsync(expectedBaseUrl, ApiKey, It.IsAny<CancellationToken>())).ReturnsAsync(response);

    private static AuthController.AbsConnectRequest KeyRequest(string? url = null) => new(url, ApiKey: ApiKey);

    private AudioYotoShelfDbContext FreshDb() => new(_options);

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    // ---- which server a request may reach ----

    [Theory]
    [InlineData(AbsUrl, null, AbsUrl)]                       // none supplied: use the configured server
    [InlineData(AbsUrl, "", AbsUrl)]
    [InlineData(AbsUrl, "   ", AbsUrl)]
    [InlineData(AbsUrl, AbsUrl, AbsUrl)]
    [InlineData(AbsUrl, "HTTP://ABS.HOME/", AbsUrl)]         // case and trailing slash do not matter
    [InlineData("http://abs.home/", null, AbsUrl)]           // configured value is normalised too
    [InlineData("  ", "http://other.example/", "http://other.example")]   // blank config = not locked
    [InlineData(null, "http://other.example/", "http://other.example")]
    public async Task Connect_ResolvesTheServerToContact(string? configured, string? requested, string expectedBaseUrl)
    {
        var rig = CreateRig(Settings(configuredUrl: configured));
        ExpectApiKeyLogin(rig, expectedBaseUrl, LoginResponse());

        var result = await rig.Controller.ConnectToAudiobookshelf(KeyRequest(requested), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        FreshDb().UserConnections.Single().AudiobookshelfUrl.Should().Be(expectedBaseUrl);
    }

    [Theory]
    [InlineData("http://attacker.example")]
    [InlineData("http://abs.home.evil.example")]   // shares a prefix with the configured server
    [InlineData("http://abs.home:8080")]
    [InlineData("https://abs.home")]
    public async Task Connect_AnyOtherServerThanTheConfiguredOne_IsRefusedWithoutContactingIt(string requested)
    {
        var rig = CreateRig(Settings());

        var result = await rig.Controller.ConnectToAudiobookshelf(KeyRequest(requested), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().Be("This app only connects to its configured Audiobookshelf server");
        rig.SignedIn.Should().BeNull();
    }

    [Fact]
    public async Task Connect_NoServerAtAll_AsksForTheUrl()
    {
        var rig = CreateRig(Settings(configuredUrl: null));

        var result = await rig.Controller.ConnectToAudiobookshelf(
            new AuthController.AbsConnectRequest(null, ApiKey: ApiKey), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().Be("Audiobookshelf server URL is required");
    }

    // ---- credentials ----

    [Fact]
    public async Task Connect_WithApiKey_StoresTheKeyAsTheTokenAndDropsAnyRefreshToken()
    {
        var existing = TestData.CreateUserConnection(username: "alice", absRefreshToken: "old-refresh");
        _db.UserConnections.Add(existing);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse(token: "should-not-be-used", refreshToken: "should-not-be-used"));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        var saved = FreshDb().UserConnections.Single();
        saved.AudiobookshelfToken.Should().Be(ApiKey);
        saved.AudiobookshelfRefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task Connect_WithPassword_StoresTheSessionTokenFromTheLogin()
    {
        var rig = CreateRig(Settings());
        rig.Abs.Setup(s => s.LoginAsync(AbsUrl, "alice", "secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginResponse(token: "session-token", refreshToken: "refresh-1"));

        await rig.Controller.ConnectToAudiobookshelf(
            new AuthController.AbsConnectRequest(null, "alice", "secret"), CancellationToken.None);

        var saved = FreshDb().UserConnections.Single();
        saved.AudiobookshelfToken.Should().Be("session-token");
        saved.AudiobookshelfRefreshToken.Should().Be("refresh-1");
    }

    // ---- new and returning users ----

    [Fact]
    public async Task Connect_NewUser_IsCreatedWithTheirServerAndDefaultLibrary()
    {
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse(username: "alice", defaultLibraryId: "lib-9"));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        var saved = FreshDb().UserConnections.Single();
        (saved.Username, saved.AudiobookshelfUrl, saved.DefaultLibraryId).Should().Be(("alice", AbsUrl, "lib-9"));
    }

    [Fact]
    public async Task Connect_ReturningUser_KeepsTheirRowAndTakesTheNewServerAndLibrary()
    {
        var existing = TestData.CreateUserConnection(username: "alice", absUrl: "http://old.example");
        existing.DefaultLibraryId = "lib-old";
        _db.UserConnections.Add(existing);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse(username: "alice", defaultLibraryId: "lib-new"));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        var saved = FreshDb().UserConnections.Single();
        (saved.Id, saved.AudiobookshelfUrl, saved.DefaultLibraryId).Should().Be((existing.Id, AbsUrl, "lib-new"));
    }

    [Fact]
    public async Task Connect_ReturningUser_KeepsTheirLibraryWhenTheServerNamesNone()
    {
        var existing = TestData.CreateUserConnection(username: "alice");
        existing.DefaultLibraryId = "lib-old";
        _db.UserConnections.Add(existing);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse(username: "alice", defaultLibraryId: null));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        FreshDb().UserConnections.Single().DefaultLibraryId.Should().Be("lib-old");
    }

    [Fact]
    public async Task Connect_MatchesTheReturningUserByUsername_NotByAnyOtherUser()
    {
        var bob = TestData.CreateUserConnection(username: "bob");
        _db.UserConnections.Add(bob);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse(username: "alice"));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        var users = FreshDb().UserConnections.ToList();
        users.Select(u => u.Username).Should().BeEquivalentTo("alice", "bob");
        users.Single(u => u.Username == "bob").AudiobookshelfToken.Should().Be(bob.AudiobookshelfToken);
    }

    [Fact]
    public async Task Connect_RecordsTheLoginAndReportsTheConnection()
    {
        var yotoUser = TestData.CreateUserConnection(username: "alice"); // has a valid Yoto connection
        _db.UserConnections.Add(yotoUser);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse(username: "alice", defaultLibraryId: "lib-1"));

        var result = await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        var db = FreshDb();
        db.LoginEvents.Should().ContainSingle().Which.UserConnectionId.Should().Be(yotoUser.Id);
        db.UserConnections.Single().LastLoginAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        var body = Json(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        body.GetProperty("UserConnectionId").GetGuid().Should().Be(yotoUser.Id);
        body.GetProperty("Username").GetString().Should().Be("alice");
        body.GetProperty("AbsConnected").GetBoolean().Should().BeTrue();
        body.GetProperty("YotoConnected").GetBoolean().Should().BeTrue();
        body.GetProperty("DefaultLibraryId").GetString().Should().Be("lib-1");
        body.GetProperty("Libraries").EnumerateArray().Select(l => l.GetString()).Should().Equal("lib-1", "lib-2");
    }

    [Fact]
    public async Task Connect_NewUser_IsReportedAsNotConnectedToYoto()
    {
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse());

        var result = await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        Json(result.Should().BeOfType<OkObjectResult>().Subject.Value)
            .GetProperty("YotoConnected").GetBoolean().Should().BeFalse();
    }

    // ---- the session that is issued ----

    [Fact]
    public async Task Connect_IssuesACookieSessionForThatUser_WithoutTheAdminRole()
    {
        var rig = CreateRig(Settings());
        ExpectApiKeyLogin(rig, AbsUrl, LoginResponse(username: "alice"));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest(), CancellationToken.None);

        var saved = FreshDb().UserConnections.Single();
        rig.SignedInScheme.Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
        rig.SignedIn!.Identity!.AuthenticationType.Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
        rig.SignedIn.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(saved.Id.ToString());
        rig.SignedIn.FindFirstValue(ClaimTypes.Name).Should().Be("alice");
        rig.SignedIn.IsInRole("Admin").Should().BeFalse();
        rig.SignedIn.Claims.Should().HaveCount(2);
    }

    // ---- admin: only an allow-listed user, signing in to the TRUSTED admin server ----

    [Theory]
    [InlineData("http://admin.home", "alice", "alice", null, true)]                       // the happy path
    [InlineData("http://admin.home/", "alice", "alice", null, true)]                      // trailing slash on the setting
    [InlineData("http://ADMIN.home", "alice", "alice", null, true)]                       // server compared case-insensitively
    [InlineData("http://admin.home", "ALICE", "alice", null, true)]                       // username compared case-insensitively
    [InlineData("http://admin.home", "alice", "bob, alice ,carol", null, true)]           // list is trimmed
    [InlineData("http://admin.home", "alice", "bob,,alice", null, true)]                  // empty entries ignored
    [InlineData("http://admin.home", "alice", null, "alice", true)]                       // ADMIN_USERNAMES fallback
    [InlineData("http://admin.home", "alice", "bob", "alice", false)]                     // Admin:Usernames wins over the fallback
    [InlineData("http://admin.home", "alice", "bob", null, false)]                        // not on the list
    [InlineData("http://admin.home", "alice", "", null, false)]                           // empty list
    [InlineData("http://admin.home", "alice", null, null, false)]                         // no list at all
    [InlineData(null, "alice", "alice", null, false)]                                     // no trusted server configured
    [InlineData("  ", "alice", "alice", null, false)]                                     // blank trusted server
    [InlineData("http://other.home", "alice", "alice", null, false)]                      // listed, but signing in elsewhere
    public async Task Connect_GrantsAdminOnlyToAListedUserOnTheTrustedServer(
        string? adminUrl, string username, string? adminUsers, string? envAdminUsers, bool expectAdmin)
    {
        // No locked server, so the request URL decides where the login goes: the trusted one above.
        var rig = CreateRig(Settings(configuredUrl: null, adminUrl: adminUrl, adminUsers: adminUsers, envAdminUsers: envAdminUsers));
        rig.Abs.Setup(s => s.AuthorizeApiKeyAsync("http://admin.home", ApiKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginResponse(username: username));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest("http://admin.home"), CancellationToken.None);

        FreshDb().UserConnections.Single().IsAdmin.Should().Be(expectAdmin);
        rig.SignedIn!.IsInRole("Admin").Should().Be(expectAdmin);
    }

    [Theory]
    [InlineData("bob", false)]      // removed from the list
    [InlineData("", false)]         // list emptied
    [InlineData(null, false)]       // list unset
    [InlineData("alice", true)]     // still listed: keeps admin
    public async Task Connect_ExistingAdminOnTheTrustedServer_FollowsTheCurrentAllowList(string? adminUsers, bool expectAdmin)
    {
        var admin = TestData.CreateUserConnection(username: "alice");
        admin.IsAdmin = true;
        _db.UserConnections.Add(admin);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings(configuredUrl: null, adminUrl: "http://admin.home", adminUsers: adminUsers));
        rig.Abs.Setup(s => s.AuthorizeApiKeyAsync("http://admin.home", ApiKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginResponse(username: "alice"));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest("http://admin.home"), CancellationToken.None);

        FreshDb().UserConnections.Single().IsAdmin.Should().Be(expectAdmin);
        rig.SignedIn!.IsInRole("Admin").Should().Be(expectAdmin);
    }

    [Fact]
    public async Task Connect_AnAdminRowReachedViaAnotherServer_NeverGetsAnAdminSession()
    {
        var admin = TestData.CreateUserConnection(username: "alice");
        admin.IsAdmin = true;
        _db.UserConnections.Add(admin);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings(configuredUrl: null, adminUrl: "http://admin.home", adminUsers: "alice"));
        rig.Abs.Setup(s => s.AuthorizeApiKeyAsync("http://forged.example", ApiKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginResponse(username: "alice"));

        await rig.Controller.ConnectToAudiobookshelf(KeyRequest("http://forged.example"), CancellationToken.None);

        rig.SignedIn!.IsInRole("Admin").Should().BeFalse();
        FreshDb().UserConnections.Single().IsAdmin.Should().BeTrue("the persisted flag is untouched; only the session is withheld");
    }

    [Fact]
    public async Task Logout_SignsTheCookieSessionOut()
    {
        var rig = CreateRig(Settings());
        rig.Authentication
            .Setup(a => a.SignOutAsync(It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<AuthenticationProperties?>()))
            .Returns(Task.CompletedTask);

        var result = await rig.Controller.Logout();

        Json(result.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("LoggedOut").GetBoolean().Should().BeTrue();
        rig.Authentication.Verify(a => a.SignOutAsync(
            It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<AuthenticationProperties?>()), Times.Once);
    }

    // =========================================================================
    // ValidateAbsToken
    // =========================================================================

    private async Task<UserConnection> AddAbsUser(
        string? refreshToken = null, DateTimeOffset? tokenExpiry = null)
    {
        var user = TestData.CreateUserConnection(
            username: "alice", absUrl: AbsUrl, absToken: "tok", absRefreshToken: refreshToken, absTokenExpiry: tokenExpiry);
        user.AudiobookshelfTokenValidatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task ValidateAbsToken_UnknownUser_Returns404()
    {
        var rig = CreateRig(Settings(), user: PrincipalFor(Guid.NewGuid()));

        (await rig.Controller.ValidateAbsToken(CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ValidateAbsToken_ValidToken_IsReportedValidAndStamped()
    {
        var user = await AddAbsUser();
        var rig = CreateRig(Settings(), user: PrincipalFor(user.Id));
        rig.Abs.Setup(s => s.ValidateTokenAsync(AbsUrl, "tok", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await rig.Controller.ValidateAbsToken(CancellationToken.None);

        Json(result.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("Valid").GetBoolean().Should().BeTrue();
        FreshDb().UserConnections.Single().AudiobookshelfTokenValidatedAt
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ValidateAbsToken_RejectedToken_IsReportedInvalidAndNotStamped()
    {
        var user = await AddAbsUser();
        var before = user.AudiobookshelfTokenValidatedAt;
        var rig = CreateRig(Settings(), user: PrincipalFor(user.Id));
        rig.Abs.Setup(s => s.ValidateTokenAsync(AbsUrl, "tok", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await rig.Controller.ValidateAbsToken(CancellationToken.None);

        Json(result.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("Valid").GetBoolean().Should().BeFalse();
        FreshDb().UserConnections.Single().AudiobookshelfTokenValidatedAt.Should().Be(before);
    }

    [Fact]
    public async Task ValidateAbsToken_RefreshTokenNoLongerAccepted_IsReportedInvalidWithoutValidating()
    {
        var user = await AddAbsUser(refreshToken: "refresh", tokenExpiry: DateTimeOffset.UtcNow.AddMinutes(-1));
        var rig = CreateRig(Settings(), user: PrincipalFor(user.Id));
        rig.Abs.Setup(s => s.RefreshTokenAsync(AbsUrl, "refresh", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("revoked"));

        var result = await rig.Controller.ValidateAbsToken(CancellationToken.None);

        Json(result.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("Valid").GetBoolean().Should().BeFalse();
        rig.Abs.Verify(s => s.ValidateTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // Yoto OAuth: the state nonce ties the callback to the connection that started it
    // =========================================================================

    private static ClaimsPrincipal PrincipalFor(Guid userConnectionId, bool isAdmin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userConnectionId.ToString()) };
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static void UseHost(AuthController controller, string scheme = "https", string host = "yoto.example")
    {
        controller.Request.Scheme = scheme;
        controller.Request.Host = new HostString(host);
    }

    private const string YotoRedirectUri = "https://yoto.example/api/auth/yoto/callback";

    [Fact]
    public async Task AuthorizeYoto_UnknownUser_Returns404()
    {
        var rig = CreateRig(Settings(), user: PrincipalFor(Guid.NewGuid()));

        (await rig.Controller.AuthorizeYoto(CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task AuthorizeYoto_StoresAFreshNonce_AndBindsItToTheUserInTheState()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        string? capturedState = null;
        var yoto = new Mock<IYotoService>(MockBehavior.Strict);
        yoto.Setup(y => y.GetAuthorizationUrl(YotoRedirectUri, It.IsAny<string>()))
            .Callback<string, string>((_, state) => capturedState = state)
            .Returns("https://login.yoto.example/authorize?x=1");
        var rig = CreateRig(Settings(), yoto.Object, PrincipalFor(user.Id));
        UseHost(rig.Controller);

        var result = await rig.Controller.AuthorizeYoto(CancellationToken.None);

        var nonce = FreshDb().UserConnections.Single().YotoDeviceCode;
        nonce.Should().MatchRegex("^[0-9a-f]{32}$");
        capturedState.Should().Be($"{user.Id}:{nonce}");
        Json(result.Should().BeOfType<OkObjectResult>().Subject.Value)
            .GetProperty("authUrl").GetString().Should().Be("https://login.yoto.example/authorize?x=1");
    }

    [Theory]
    [InlineData("no-colon-here")]
    [InlineData("not-a-guid:nonce")]
    public async Task YotoCallback_MalformedState_IsRejectedWithoutTouchingYoto(string state)
    {
        var rig = CreateRig(Settings(), new Mock<IYotoService>(MockBehavior.Strict).Object);

        var result = await rig.Controller.YotoCallback("code-1", state, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().Be("Invalid state parameter");
    }

    [Fact]
    public async Task YotoCallback_UnknownConnection_Returns404()
    {
        var rig = CreateRig(Settings(), new Mock<IYotoService>(MockBehavior.Strict).Object);

        var result = await rig.Controller.YotoCallback("code-1", $"{Guid.NewGuid()}:nonce", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task YotoCallback_WrongNonce_IsRejectedAsPossibleCsrf_WithoutExchangingTheCode()
    {
        var user = TestData.CreateUserConnection();
        user.YotoDeviceCode = "expected-nonce";
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings(), new Mock<IYotoService>(MockBehavior.Strict).Object);

        var result = await rig.Controller.YotoCallback("code-1", $"{user.Id}:forged-nonce", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().Be("State mismatch — possible CSRF attack");
        FreshDb().UserConnections.Single().YotoDeviceCode.Should().Be("expected-nonce");
    }

    [Fact]
    public async Task YotoCallback_GoodNonce_ExchangesTheCode_StoresTokens_AndClearsTheNonce()
    {
        var user = TestData.CreateUserConnection(yotoAccessToken: null, yotoRefreshToken: null);
        user.YotoTokenExpiresAt = null;
        user.YotoDeviceCode = "abc:def";   // a nonce may itself contain a colon
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        var yoto = new Mock<IYotoService>(MockBehavior.Strict);
        yoto.Setup(y => y.ExchangeAuthCodeAsync("code-1", YotoRedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YotoTokenResponse("access-1", "refresh-1", "Bearer", 3600));
        var rig = CreateRig(Settings(), yoto.Object);
        UseHost(rig.Controller);

        var result = await rig.Controller.YotoCallback("code-1", $"{user.Id}:abc:def", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/setup");
        var saved = FreshDb().UserConnections.Single();
        saved.YotoAccessToken.Should().Be("access-1");
        saved.YotoRefreshToken.Should().Be("refresh-1");
        saved.YotoTokenExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
        saved.YotoDeviceCode.Should().BeNull();
    }

    // =========================================================================
    // Settings and status: persisted, and the admin flag reflects the SESSION
    // =========================================================================

    [Fact]
    public async Task UpdateSettings_PersistsTheChange()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings(), user: PrincipalFor(user.Id));

        await rig.Controller.UpdateSettings(new UpdateSettingsRequest("lib-7", 2, 9), CancellationToken.None);

        var saved = FreshDb().UserConnections.Single();
        (saved.DefaultLibraryId, saved.DefaultMinAge, saved.DefaultMaxAge).Should().Be(("lib-7", 2, 9));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_ReportsAdminFromTheSessionRole_NotTheStoredFlag(bool sessionIsAdmin)
    {
        var user = TestData.CreateUserConnection();
        user.IsAdmin = !sessionIsAdmin;   // the stored flag deliberately disagrees with the session
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        var rig = CreateRig(Settings(), user: PrincipalFor(user.Id, isAdmin: sessionIsAdmin));

        var status = await rig.Controller.GetConnectionStatus(CancellationToken.None);
        var updated = await rig.Controller.UpdateSettings(new UpdateSettingsRequest(), CancellationToken.None);

        Json(status.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("IsAdmin").GetBoolean().Should().Be(sessionIsAdmin);
        Json(updated.Should().BeOfType<OkObjectResult>().Subject.Value).GetProperty("IsAdmin").GetBoolean().Should().Be(sessionIsAdmin);
    }
}
