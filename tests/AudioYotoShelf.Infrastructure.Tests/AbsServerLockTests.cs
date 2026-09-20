using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class AbsServerLockTests : IDisposable
{
    private readonly InMemoryDbFixture _dbFixture = new();

    public void Dispose() => _dbFixture.Dispose();

    /// <summary>
    /// Every request after connect reads the URL stored on the connection, so a row created before
    /// the server URL was locked would keep fetching from its old server forever. Startup repoints
    /// them, which is what makes the lock hold beyond connect time.
    /// </summary>
    [Fact]
    public async Task RepointConnectionsAsync_MovesStaleConnectionsToTheConfiguredServer()
    {
        var db = _dbFixture.DbContext;
        var stale = TestData.CreateUserConnection(username: "alice", absUrl: "http://old.example");
        var alreadyCorrect = TestData.CreateUserConnection(username: "bob", absUrl: "http://abs.home");
        db.UserConnections.AddRange(stale, alreadyCorrect);
        await db.SaveChangesAsync();

        var repointed = await AbsServerLock.RepointConnectionsAsync(
            db, "http://abs.home", Mock.Of<ILogger>(), CancellationToken.None);

        repointed.Should().Be(1);
        (await db.UserConnections.FindAsync(stale.Id))!.AudiobookshelfUrl.Should().Be("http://abs.home");
    }

    [Fact]
    public async Task RepointConnectionsAsync_IgnoresCaseAndTrailingSlash()
    {
        var db = _dbFixture.DbContext;
        db.UserConnections.Add(TestData.CreateUserConnection(absUrl: "http://ABS.home/"));
        await db.SaveChangesAsync();

        var repointed = await AbsServerLock.RepointConnectionsAsync(
            db, "http://abs.home", Mock.Of<ILogger>(), CancellationToken.None);

        repointed.Should().Be(0);
    }
}
