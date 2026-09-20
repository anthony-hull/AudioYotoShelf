using AudioYotoShelf.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AudioYotoShelf.Api.Tests;

internal static class TestDatabases
{
    /// <summary>
    /// A real Npgsql context aimed at a closed loopback port. EF's CanConnectAsync answers false (it does
    /// not throw) when the server is unreachable, which an in-memory provider can never do. It needs no
    /// server, but it does attempt a loopback connection.
    /// </summary>
    public static AudioYotoShelfDbContext UnreachablePostgres() =>
        new(new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2;Pooling=false").Options);
}
