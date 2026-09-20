using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Admin;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AudioYotoShelf.Api.Tests;

public class AdminControllerTests : IDisposable
{
    private readonly AudioYotoShelfDbContext _db;
    private readonly AdminController _sut;

    public AdminControllerTests()
    {
        var options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"AdminCtrlTest_{Guid.NewGuid()}")
            .Options;
        _db = new AudioYotoShelfDbContext(options);
        _sut = new AdminController(_db);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task Overview_AggregatesUsersLoginsAndTransfers()
    {
        var alice = TestData.CreateUserConnection(username: "alice");
        alice.IsAdmin = true;
        alice.LastLoginAt = DateTimeOffset.UtcNow;
        var bob = TestData.CreateUserConnection(username: "bob");
        _db.UserConnections.AddRange(alice, bob);

        _db.LoginEvents.AddRange(
            new LoginEvent { UserConnectionId = alice.Id },
            new LoginEvent { UserConnectionId = alice.Id },
            new LoginEvent { UserConnectionId = bob.Id });

        var completed = TestData.CreateCardTransfer(alice.Id, "item-a");
        completed.Status = TransferStatus.Completed;
        var failed = TestData.CreateCardTransfer(alice.Id, "item-b");
        failed.Status = TransferStatus.Failed;
        _db.CardTransfers.AddRange(completed, failed);
        await _db.SaveChangesAsync();

        var result = await _sut.Overview(CancellationToken.None);

        var overview = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<AdminOverview>().Subject;
        overview.TotalUsers.Should().Be(2);
        overview.AdminUsers.Should().Be(1);
        overview.ActiveUsers7d.Should().Be(1);
        overview.TotalLogins.Should().Be(3);
        overview.TotalTransfers.Should().Be(2);
        overview.CompletedTransfers.Should().Be(1);
        overview.FailedTransfers.Should().Be(1);
        overview.TransferSuccessRate.Should().Be(50);
    }

    [Fact]
    public async Task Users_ReturnsPerUserCounts()
    {
        var alice = TestData.CreateUserConnection(username: "alice");
        _db.UserConnections.Add(alice);
        _db.LoginEvents.Add(new LoginEvent { UserConnectionId = alice.Id });
        _db.CardTransfers.Add(TestData.CreateCardTransfer(alice.Id, "item-a"));
        await _db.SaveChangesAsync();

        var result = await _sut.Users(CancellationToken.None);

        var rows = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<AdminUserRow>>().Subject.ToList();
        rows.Should().HaveCount(1);
        rows[0].Username.Should().Be("alice");
        rows[0].LoginCount.Should().Be(1);
        rows[0].TransferCount.Should().Be(1);
    }

    [Fact]
    public async Task Usage_ReturnsRequestedNumberOfDays()
    {
        var result = await _sut.Usage(days: 7, CancellationToken.None);

        var points = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<UsagePoint>>().Subject.ToList();
        points.Should().HaveCount(7);
    }

    // =========================================================================
    // Mutation-testing additions — time windows, filters, ordering and the day series
    // =========================================================================

    /// <summary>
    /// The controller reads the clock itself, so dates seeded from "today" would disagree with it if UTC midnight
    /// fell mid-test. Waiting out the last few seconds of the day is cheaper than a test that fails once a night.
    /// </summary>
    private static async Task<DateOnly> StableUtcTodayAsync()
    {
        var now = DateTime.UtcNow;
        var untilMidnight = now.Date.AddDays(1) - now;
        if (untilMidnight < TimeSpan.FromSeconds(5))
            await Task.Delay(untilMidnight + TimeSpan.FromMilliseconds(50));
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static DateTimeOffset DaysAgo(double days) => DateTimeOffset.UtcNow.AddDays(-days);

    private async Task<T> GetOk<T>(Task<IActionResult> action) where T : class
    {
        var result = await action;
        return result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<T>().Subject;
    }

    [Fact]
    public async Task Overview_CountsEachFigureFromItsOwnSliceOfTheData()
    {
        var alice = TestData.CreateUserConnection(username: "alice");
        alice.IsAdmin = true;
        alice.LastLoginAt = DaysAgo(3);
        var bob = TestData.CreateUserConnection(username: "bob", yotoRefreshToken: null);
        bob.AudiobookshelfToken = null;
        bob.LastLoginAt = DaysAgo(20);
        var carol = TestData.CreateUserConnection(username: "carol");
        carol.LastLoginAt = DaysAgo(40);
        var dave = TestData.CreateUserConnection(username: "dave"); // never logged in
        _db.UserConnections.AddRange(alice, bob, carol, dave);

        _db.LoginEvents.AddRange(
            new LoginEvent { UserConnectionId = alice.Id, CreatedAt = DaysAgo(3) },
            new LoginEvent { UserConnectionId = bob.Id, CreatedAt = DaysAgo(20) },
            new LoginEvent { UserConnectionId = carol.Id, CreatedAt = DaysAgo(40) });

        _db.CardTransfers.AddRange(
            Transfer(alice, "a", TransferStatus.Completed, DaysAgo(3)),
            Transfer(alice, "b", TransferStatus.Completed, DaysAgo(20)),
            Transfer(bob, "c", TransferStatus.Failed, DaysAgo(3)),
            Transfer(bob, "d", TransferStatus.Pending, DaysAgo(40)),
            Transfer(bob, "e", TransferStatus.Pending, DaysAgo(1)));   // 3 in the 7-day window, 2 outside it
        _db.Playlists.AddRange(
            new Playlist { Name = "One", UserConnectionId = alice.Id },
            new Playlist { Name = "Two", UserConnectionId = bob.Id });
        await _db.SaveChangesAsync();

        var overview = await GetOk<AdminOverview>(_sut.Overview(CancellationToken.None));

        overview.Should().Be(new AdminOverview(
            TotalUsers: 4, AbsConnectedUsers: 3, YotoConnectedUsers: 3, AdminUsers: 1,
            ActiveUsers7d: 1, ActiveUsers30d: 2,
            TotalLogins: 3, Logins7d: 1, Logins30d: 2,
            TotalTransfers: 5, CompletedTransfers: 2, FailedTransfers: 1,
            TransferSuccessRate: 40, Transfers7d: 3,
            TotalPlaylists: 2));
    }

    [Fact]
    public async Task Overview_RoundsSuccessRateToOneDecimalPlace()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        _db.CardTransfers.AddRange(
            Transfer(user, "a", TransferStatus.Completed, DaysAgo(1)),
            Transfer(user, "b", TransferStatus.Failed, DaysAgo(1)),
            Transfer(user, "c", TransferStatus.Failed, DaysAgo(1)));
        await _db.SaveChangesAsync();

        var overview = await GetOk<AdminOverview>(_sut.Overview(CancellationToken.None));

        overview.TransferSuccessRate.Should().Be(33.3);
    }

    [Fact]
    public async Task Overview_EmptyDatabase_ReportsZerosRatherThanNaN()
    {
        var overview = await GetOk<AdminOverview>(_sut.Overview(CancellationToken.None));

        overview.Should().Be(new AdminOverview(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Users_ListsMostRecentLoginFirst_WithConnectionFlagsAndCounts()
    {
        var recent = TestData.CreateUserConnection(username: "recent");
        recent.LastLoginAt = DaysAgo(1);
        recent.IsAdmin = true;
        var older = TestData.CreateUserConnection(username: "older", yotoRefreshToken: null);
        older.LastLoginAt = DaysAgo(9);
        older.AudiobookshelfToken = null;
        var never = TestData.CreateUserConnection(username: "never");
        _db.UserConnections.AddRange(older, never, recent);
        _db.LoginEvents.AddRange(
            new LoginEvent { UserConnectionId = recent.Id },
            new LoginEvent { UserConnectionId = recent.Id });
        _db.CardTransfers.Add(Transfer(older, "a", TransferStatus.Pending, DaysAgo(1)));
        await _db.SaveChangesAsync();

        var rows = (await GetOk<IEnumerable<AdminUserRow>>(_sut.Users(CancellationToken.None))).ToList();

        rows.Select(r => r.Username).Should().Equal("recent", "older", "never");
        rows[0].Should().Match<AdminUserRow>(r => r.IsAdmin && r.AbsConnected && r.YotoConnected && r.LoginCount == 2 && r.TransferCount == 0);
        rows[1].Should().Match<AdminUserRow>(r => !r.IsAdmin && !r.AbsConnected && !r.YotoConnected && r.LoginCount == 0 && r.TransferCount == 1);
        rows[2].LastLoginAt.Should().BeNull();
    }

    [Fact]
    public async Task Usage_BucketsLoginsAndTransfersByUtcDay_OverTheRequestedWindow()
    {
        var today = await StableUtcTodayAsync();
        DateTimeOffset At(int daysBack, int hour, int minute = 0, int second = 0) =>
            new(today.AddDays(-daysBack).ToDateTime(new TimeOnly(hour, minute, second)), TimeSpan.Zero);

        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        _db.LoginEvents.AddRange(
            new LoginEvent { UserConnectionId = user.Id, CreatedAt = At(0, 1) },
            new LoginEvent { UserConnectionId = user.Id, CreatedAt = At(0, 2) },
            new LoginEvent { UserConnectionId = user.Id, CreatedAt = At(1, 23, 30) },
            // 02:00 at +05:00 on today's date is 21:00 UTC the previous day: it belongs to yesterday.
            new LoginEvent { UserConnectionId = user.Id, CreatedAt = new DateTimeOffset(today.ToDateTime(new TimeOnly(2, 0)), TimeSpan.FromHours(5)) },
            new LoginEvent { UserConnectionId = user.Id, CreatedAt = At(2, 0) },          // first instant of the window: in
            new LoginEvent { UserConnectionId = user.Id, CreatedAt = At(3, 23, 59, 59) }); // last second before it: out
        _db.CardTransfers.AddRange(
            Transfer(user, "a", TransferStatus.Pending, At(0, 12)),
            Transfer(user, "b", TransferStatus.Pending, At(2, 8)),
            Transfer(user, "c", TransferStatus.Pending, At(2, 9)),
            Transfer(user, "first-instant", TransferStatus.Pending, At(2, 0)),       // first instant of the window: in
            Transfer(user, "d", TransferStatus.Pending, At(3, 23, 59, 59)));         // last second before it: out
        await _db.SaveChangesAsync();

        var points = (await GetOk<IEnumerable<UsagePoint>>(_sut.Usage(days: 3, CancellationToken.None))).ToList();

        points.Should().Equal(
            new UsagePoint(today.AddDays(-2), Logins: 1, Transfers: 3),
            new UsagePoint(today.AddDays(-1), Logins: 2, Transfers: 0),
            new UsagePoint(today, Logins: 2, Transfers: 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(90, 90)]
    [InlineData(1000, 90)]
    public async Task Usage_ClampsTheWindowToBetweenOneAndNinetyDays(int requested, int expectedPoints)
    {
        var today = await StableUtcTodayAsync();   // before the call: the controller reads the clock during it

        var points = (await GetOk<IEnumerable<UsagePoint>>(_sut.Usage(requested, CancellationToken.None))).ToList();

        points.Should().HaveCount(expectedPoints);
        points[0].Date.Should().Be(today.AddDays(-(expectedPoints - 1)));
        points[^1].Date.Should().Be(today);
    }

    [Fact]
    public async Task Usage_DefaultsToFourteenDays()
    {
        var points = (await GetOk<IEnumerable<UsagePoint>>(_sut.Usage())).ToList();

        points.Should().HaveCount(14);
    }

    private static CardTransfer Transfer(UserConnection user, string itemId, TransferStatus status, DateTimeOffset createdAt)
    {
        var transfer = TestData.CreateCardTransfer(user.Id, itemId, status: status);
        transfer.CreatedAt = createdAt;
        return transfer;
    }
}
