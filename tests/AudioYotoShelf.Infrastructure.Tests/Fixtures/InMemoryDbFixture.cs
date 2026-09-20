using AudioYotoShelf.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AudioYotoShelf.Infrastructure.Tests.Fixtures;

/// <summary>
/// Provides a fresh InMemory database for each test.
/// Ensures test isolation without requiring a real PostgreSQL instance.
/// </summary>
public class InMemoryDbFixture : IDisposable
{
    private readonly DbContextOptions<AudioYotoShelfDbContext> _options;

    public AudioYotoShelfDbContext DbContext { get; }

    public InMemoryDbFixture()
    {
        _options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        DbContext = new AudioYotoShelfDbContext(_options);
        DbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// A fresh context on the same database. Reading through it proves a change was saved, which
    /// <c>DbContext.FindAsync</c> cannot: that returns the tracked instance without touching the store.
    /// </summary>
    public AudioYotoShelfDbContext NewContext() => new(_options);

    public void Dispose()
    {
        DbContext.Database.EnsureDeleted();
        DbContext.Dispose();
    }
}
