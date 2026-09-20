using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class YotoTokensTests : IDisposable
{
    private readonly InMemoryDbFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<Core.Entities.UserConnection> SeedUserAsync(DateTimeOffset? expiry, string? refreshToken = "refresh-old")
    {
        var user = TestData.CreateUserConnection(
            yotoAccessToken: "access-old", yotoRefreshToken: refreshToken, yotoTokenExpiry: expiry);
        user.YotoTokenExpiresAt = expiry;
        _fixture.DbContext.UserConnections.Add(user);
        await _fixture.DbContext.SaveChangesAsync();
        return user;
    }

    private static Mock<IYotoService> ServiceReturning(YotoTokenResponse response)
    {
        var service = new Mock<IYotoService>();
        service.Setup(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        return service;
    }

    private Task<string> EnsureValidAsync(IYotoService service, Core.Entities.UserConnection user) =>
        YotoTokens.EnsureValidAsync(_fixture.DbContext, service, user, Mock.Of<ILogger>(), CancellationToken.None);

    [Fact]
    public async Task EnsureValid_TokenOutsideTheFiveMinuteWindow_ReturnsItWithoutRefreshing()
    {
        var user = await SeedUserAsync(DateTimeOffset.UtcNow.AddMinutes(6));

        var token = await EnsureValidAsync(new Mock<IYotoService>(MockBehavior.Strict).Object, user);

        token.Should().Be("access-old");
    }

    [Theory]
    [InlineData(4)]     // inside the window
    [InlineData(-30)]   // already expired
    public async Task EnsureValid_TokenInsideTheWindow_Refreshes(int minutesToExpiry)
    {
        var user = await SeedUserAsync(DateTimeOffset.UtcNow.AddMinutes(minutesToExpiry));
        var service = ServiceReturning(new("access-new", "refresh-new", "Bearer", 3600));

        var token = await EnsureValidAsync(service.Object, user);

        token.Should().Be("access-new");
        service.Verify(s => s.RefreshTokenAsync("refresh-old", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureValid_NoKnownExpiry_Refreshes()
    {
        var user = await SeedUserAsync(expiry: null);
        var service = ServiceReturning(new("access-new", "refresh-new", "Bearer", 3600));

        (await EnsureValidAsync(service.Object, user)).Should().Be("access-new");
    }

    [Fact]
    public async Task EnsureValid_StoresTheRotatedRefreshTokenAndANewExpiry()
    {
        var user = await SeedUserAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        var service = ServiceReturning(new("access-new", "refresh-new", "Bearer", 7200));

        await EnsureValidAsync(service.Object, user);

        user.YotoAccessToken.Should().Be("access-new");
        user.YotoRefreshToken.Should().Be("refresh-new");
        user.YotoTokenExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(7200), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task EnsureValid_RefreshResponseWithoutARefreshToken_KeepsTheStoredOne()
    {
        var user = await SeedUserAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        var service = ServiceReturning(new("access-new", null, "Bearer", 3600));

        await EnsureValidAsync(service.Object, user);

        user.YotoRefreshToken.Should().Be("refresh-old");
    }

    [Fact]
    public async Task EnsureValid_PersistsTheRefreshedTokens()
    {
        var user = await SeedUserAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        var service = ServiceReturning(new("access-new", "refresh-new", "Bearer", 3600));

        await EnsureValidAsync(service.Object, user);

        await using var other = _fixture.NewContext();
        var stored = await other.UserConnections.FindAsync(user.Id);
        (stored!.YotoAccessToken, stored.YotoRefreshToken).Should().Be(("access-new", "refresh-new"));
    }
}
