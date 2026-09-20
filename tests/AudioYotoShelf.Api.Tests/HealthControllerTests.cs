using System.Text;
using System.Text.Json;
using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class HealthControllerTests
{
    private static AudioYotoShelfDbContext WorkingDb() =>
        new(new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"Health_{Guid.NewGuid()}").Options);

    private static AudioYotoShelfDbContext DisposedDb()
    {
        var db = WorkingDb();
        db.Dispose();
        return db;
    }

    private static IDistributedCache WorkingCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static IChapterExtractor Ffmpeg(bool isAvailable)
    {
        var ffmpeg = new Mock<IChapterExtractor>();
        ffmpeg.Setup(f => f.IsFfmpegAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(isAvailable);
        return ffmpeg.Object;
    }

    /// <summary>A cache whose Get returns <paramref name="stored"/> (null = nothing) and records what was Set.</summary>
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

    private static async Task<(int Status, string Json)> GetHealthAsync(
        AudioYotoShelfDbContext db, IDistributedCache cache, IChapterExtractor ffmpeg)
    {
        var result = await new HealthController(db, cache, ffmpeg).Get(CancellationToken.None);

        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        return (objectResult.StatusCode ?? 200, JsonSerializer.Serialize(objectResult.Value));
    }

    [Fact]
    public async Task Get_EverythingHealthy_Returns200ListingEachDependency()
    {
        var (status, json) = await GetHealthAsync(WorkingDb(), WorkingCache(), Ffmpeg(true));

        status.Should().Be(200);
        json.Should().Be("""{"postgres":{"status":"healthy"},"redis":{"status":"healthy"},"ffmpeg":{"status":"healthy"}}""");
    }

    [Fact]
    public async Task Get_FfmpegMissing_StillReturns200ButReportsItUnavailable()
    {
        var (status, json) = await GetHealthAsync(WorkingDb(), WorkingCache(), Ffmpeg(false));

        status.Should().Be(200);
        json.Should().Contain("\"ffmpeg\":{\"status\":\"unavailable\"}");
    }

    [Fact]
    public async Task Get_PostgresThrows_Returns503WithTheErrorWhileOthersStayHealthy()
    {
        var (status, json) = await GetHealthAsync(DisposedDb(), WorkingCache(), Ffmpeg(true));

        status.Should().Be(503);
        using var doc = JsonDocument.Parse(json);
        var postgres = doc.RootElement.GetProperty("postgres");
        postgres.GetProperty("status").GetString().Should().Be("unhealthy");
        postgres.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("redis").GetProperty("status").GetString().Should().Be("healthy");
    }

    [Fact]
    public async Task Get_RedisThrows_Returns503WithTheError()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var (status, json) = await GetHealthAsync(WorkingDb(), cache.Object, Ffmpeg(true));

        status.Should().Be(503);
        json.Should().Contain("\"redis\":{\"status\":\"unhealthy\",\"error\":\"redis is down\"}");
    }

    [Theory]
    [InlineData("not-ok")]
    [InlineData(null)]
    public async Task Get_RedisReadsBackSomethingElse_Returns503WithoutAnError(string? stored)
    {
        var (status, json) = await GetHealthAsync(WorkingDb(), CacheReturning(stored).Object, Ffmpeg(true));

        status.Should().Be(503);
        json.Should().Contain("\"redis\":{\"status\":\"unhealthy\"}");
    }

    [Fact]
    public async Task Get_RoundTripsTheHealthCheckKeyThatExpiresInFiveSeconds()
    {
        var cache = CacheReturning("ok");

        await GetHealthAsync(WorkingDb(), cache.Object, Ffmpeg(true));

        cache.Verify(c => c.SetAsync(
            "health_check",
            It.Is<byte[]>(value => Encoding.UTF8.GetString(value) == "ok"),
            It.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromSeconds(5)),
            It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(c => c.GetAsync("health_check", It.IsAny<CancellationToken>()), Times.Once);
    }
}
