using System.Text;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class AbsTokensTests : IDisposable
{
    private readonly InMemoryDbFixture _dbFixture = new();

    public void Dispose() => _dbFixture.Dispose();

    /// <summary>Builds a minimal JWT whose payload carries the given exp claim (signature unused).</summary>
    private static string MakeJwt(DateTimeOffset exp)
    {
        static string B64Url(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64Url("{\"alg\":\"HS256\"}")}.{B64Url($"{{\"exp\":{exp.ToUnixTimeSeconds()}}}")}.sig";
    }

    private static AbsLoginResponse LoginResponse(string accessToken, string? refreshToken) =>
        new(new AbsUser("u1", "testuser", "user", "legacy", true, null, null, accessToken, refreshToken), null);

    // =========================================================================
    // GetJwtExpiry
    // =========================================================================

    [Fact]
    public void GetJwtExpiry_ParsesExpClaim()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(12);

        var result = AbsTokens.GetJwtExpiry(MakeJwt(exp));

        result.Should().NotBeNull();
        result!.Value.ToUnixTimeSeconds().Should().Be(exp.ToUnixTimeSeconds());
    }

    [Fact]
    public void GetJwtExpiry_OpaqueToken_ReturnsNull()
    {
        AbsTokens.GetJwtExpiry("abs-opaque-token").Should().BeNull();
    }

    // =========================================================================
    // ApplyLogin
    // =========================================================================

    [Fact]
    public void ApplyLogin_PrefersAccessTokenAndCapturesRefreshAndExpiry()
    {
        var user = TestData.CreateUserConnection(absToken: "old");
        var exp = DateTimeOffset.UtcNow.AddHours(12);
        var jwt = MakeJwt(exp);
        var absUser = new AbsUser("u1", "testuser", "user", "legacy-token", true, null, null, jwt, "refresh-1");

        AbsTokens.ApplyLogin(user, absUser);

        user.AudiobookshelfToken.Should().Be(jwt);
        user.AudiobookshelfRefreshToken.Should().Be("refresh-1");
        user.AudiobookshelfTokenExpiresAt!.Value.ToUnixTimeSeconds().Should().Be(exp.ToUnixTimeSeconds());
    }

    [Fact]
    public void ApplyLogin_LegacyTokenOnly_NoExpiryNoRefresh()
    {
        var user = TestData.CreateUserConnection(absToken: "old");
        var absUser = new AbsUser("u1", "testuser", "user", "opaque-legacy", true, null, null);

        AbsTokens.ApplyLogin(user, absUser);

        user.AudiobookshelfToken.Should().Be("opaque-legacy");
        user.AudiobookshelfTokenExpiresAt.Should().BeNull();
        user.AudiobookshelfRefreshToken.Should().BeNull();
    }

    [Fact]
    public void ApplyLogin_RefreshOmittingRotatedToken_KeepsStoredRefreshToken()
    {
        var user = TestData.CreateUserConnection(absToken: "old", absRefreshToken: "kept-refresh");
        var absUser = new AbsUser("u1", "testuser", "user", "legacy", true, null, null,
            MakeJwt(DateTimeOffset.UtcNow.AddHours(12)), RefreshToken: null);

        AbsTokens.ApplyLogin(user, absUser);

        user.AudiobookshelfRefreshToken.Should().Be("kept-refresh");
    }

    // =========================================================================
    // ApplyApiKey
    // =========================================================================

    [Fact]
    public void ApplyApiKey_StoresKeyAsTokenAndDropsPasswordLoginRefreshToken()
    {
        // Switching a connection from password login to an API key must not leave the old refresh
        // token behind, or EnsureValidAsync would later swap the key for a password-session token.
        var user = TestData.CreateUserConnection(absToken: "old-access", absRefreshToken: "old-refresh");
        var apiKey = MakeJwt(DateTimeOffset.UtcNow.AddDays(30));

        AbsTokens.ApplyApiKey(user, apiKey);

        user.AudiobookshelfToken.Should().Be(apiKey);
        user.AudiobookshelfRefreshToken.Should().BeNull();
    }

    [Fact]
    public void ApplyApiKey_KeyWithExpiry_RecordsExpiry()
    {
        var user = TestData.CreateUserConnection();
        var exp = DateTimeOffset.UtcNow.AddDays(30);

        AbsTokens.ApplyApiKey(user, MakeJwt(exp));

        user.AudiobookshelfTokenExpiresAt!.Value.ToUnixTimeSeconds().Should().Be(exp.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ApplyApiKey_ThenEnsureValid_ReturnsKeyWithoutRefreshing()
    {
        // An API key has no refresh token, so even one close to expiry is used as-is.
        var user = TestData.CreateUserConnection();
        var apiKey = MakeJwt(DateTimeOffset.UtcNow.AddMinutes(1));
        AbsTokens.ApplyApiKey(user, apiKey);
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);

        var token = await AbsTokens.EnsureValidAsync(
            _dbFixture.DbContext, absService.Object, user, Mock.Of<ILogger>(), CancellationToken.None);

        token.Should().Be(apiKey);
    }

    // =========================================================================
    // EnsureValidAsync
    // =========================================================================

    [Fact]
    public async Task EnsureValidAsync_RefreshesWhenNearExpiryAndPersists()
    {
        var db = _dbFixture.DbContext;
        var user = TestData.CreateUserConnection(
            absToken: "stale",
            absRefreshToken: "refresh-old",
            absTokenExpiry: DateTimeOffset.UtcNow.AddMinutes(1)); // inside the 5-minute window
        db.UserConnections.Add(user);
        await db.SaveChangesAsync();

        var newAccess = MakeJwt(DateTimeOffset.UtcNow.AddHours(12));
        var absService = new Mock<IAudiobookshelfService>();
        absService.Setup(s => s.RefreshTokenAsync("http://abs.local", "refresh-old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginResponse(newAccess, "refresh-new"));

        var token = await AbsTokens.EnsureValidAsync(
            db, absService.Object, user, Mock.Of<ILogger>(), CancellationToken.None);

        token.Should().Be(newAccess);
        user.AudiobookshelfToken.Should().Be(newAccess);
        user.AudiobookshelfRefreshToken.Should().Be("refresh-new");

        var reloaded = await db.UserConnections.FindAsync(user.Id);
        reloaded!.AudiobookshelfToken.Should().Be(newAccess);
        absService.Verify(s => s.RefreshTokenAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureValidAsync_NoRefreshToken_ReturnsExistingTokenWithoutCallingService()
    {
        var user = TestData.CreateUserConnection(absToken: "opaque"); // no refresh token, no expiry
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);

        var token = await AbsTokens.EnsureValidAsync(
            _dbFixture.DbContext, absService.Object, user, Mock.Of<ILogger>(), CancellationToken.None);

        token.Should().Be("opaque");
    }

    [Fact]
    public async Task EnsureValidAsync_TokenStillFresh_DoesNotRefresh()
    {
        var user = TestData.CreateUserConnection(
            absToken: "fresh",
            absRefreshToken: "refresh",
            absTokenExpiry: DateTimeOffset.UtcNow.AddHours(6));
        var absService = new Mock<IAudiobookshelfService>(MockBehavior.Strict);

        var token = await AbsTokens.EnsureValidAsync(
            _dbFixture.DbContext, absService.Object, user, Mock.Of<ILogger>(), CancellationToken.None);

        token.Should().Be("fresh");
    }
}
