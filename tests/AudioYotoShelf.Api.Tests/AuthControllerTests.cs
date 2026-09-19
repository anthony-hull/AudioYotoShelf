using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class AuthControllerTests : IDisposable
{
    private readonly AudioYotoShelfDbContext _db;
    private readonly AuthController _sut;

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
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<AuthController>>());
    }

    private AuthController CreateSut(IAudiobookshelfService absService, string? configuredAbsUrl)
    {
        var settings = new Dictionary<string, string?> { ["Audiobookshelf:Url"] = configuredAbsUrl };
        var sut = new AuthController(
            absService,
            Mock.Of<IYotoService>(),
            _db,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            Mock.Of<ILogger<AuthController>>());
        sut.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
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
