using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
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
    private readonly AudioYotoShelfDbContext _db;
    private readonly AuthController _sut;
    private readonly Mock<ISsoFlowStore> _flows = new();
    private readonly Mock<IAuthenticationService> _authentication = new();

    public AuthControllerTests()
    {
        var options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"AuthCtrlTest_{Guid.NewGuid()}")
            .Options;
        _db = new AudioYotoShelfDbContext(options);

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
}
