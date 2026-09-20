using System.Text;
using AudioYotoShelf.Api.Health;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class HealthChecksTests
{
    private static readonly HealthCheckContext Context = new();

    private static AudioYotoShelfDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"HealthChecks_{Guid.NewGuid()}").Options);

    /// <summary>
    /// A real Npgsql context aimed at a closed loopback port. EF's CanConnectAsync answers false (it does
    /// not throw) when the server is unreachable, which an in-memory provider can never do.
    /// </summary>
    private static AudioYotoShelfDbContext UnreachablePostgres() =>
        new(new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2;Pooling=false").Options);

    private static Mock<IDistributedCache> CacheReturning(string? stored)
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored is null ? null : Encoding.UTF8.GetBytes(stored));
        return cache;
    }

    // =========================================================================
    // PostgresHealthCheck
    // =========================================================================

    [Fact]
    public async Task Postgres_ReachableDatabase_IsHealthy()
    {
        var result = await new PostgresHealthCheck(InMemoryDb()).CheckHealthAsync(Context);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Postgres_ConnectAnswersFalse_IsUnhealthyWithoutAnException()
    {
        using var db = UnreachablePostgres();

        var result = await new PostgresHealthCheck(db).CheckHealthAsync(Context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task Postgres_ConnectThrows_IsUnhealthyCarryingTheException()
    {
        var db = InMemoryDb();
        db.Dispose();

        var result = await new PostgresHealthCheck(db).CheckHealthAsync(Context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<ObjectDisposedException>();
    }

    // =========================================================================
    // RedisHealthCheck
    // =========================================================================

    [Fact]
    public async Task Redis_ReadsBackWhatItWrote_IsHealthy()
    {
        var result = await new RedisHealthCheck(CacheReturning("ok").Object).CheckHealthAsync(Context);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData("different")]
    [InlineData(null)]
    public async Task Redis_ReadsBackSomethingElse_IsUnhealthyWithoutAnException(string? stored)
    {
        var result = await new RedisHealthCheck(CacheReturning(stored).Object).CheckHealthAsync(Context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task Redis_Throws_IsUnhealthyCarryingTheException()
    {
        var failure = new InvalidOperationException("redis is down");
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var result = await new RedisHealthCheck(cache.Object).CheckHealthAsync(Context);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task Redis_RoundTripsTheHealthCheckKeyThatExpiresInFiveSeconds()
    {
        var cache = CacheReturning("ok");

        await new RedisHealthCheck(cache.Object).CheckHealthAsync(Context);

        cache.Verify(c => c.SetAsync(
            "health_check",
            It.Is<byte[]>(value => Encoding.UTF8.GetString(value) == "ok"),
            It.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromSeconds(5)),
            It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(c => c.GetAsync("health_check", It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // FfmpegHealthCheck: a missing ffmpeg degrades readiness, it does not fail it
    // =========================================================================

    [Theory]
    [InlineData(true, HealthStatus.Healthy)]
    [InlineData(false, HealthStatus.Degraded)]
    public async Task Ffmpeg_StatusFollowsAvailability(bool isAvailable, HealthStatus expected)
    {
        var ffmpeg = new Mock<IChapterExtractor>();
        ffmpeg.Setup(f => f.IsFfmpegAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(isAvailable);

        var result = await new FfmpegHealthCheck(ffmpeg.Object).CheckHealthAsync(Context);

        result.Status.Should().Be(expected);
    }
}
