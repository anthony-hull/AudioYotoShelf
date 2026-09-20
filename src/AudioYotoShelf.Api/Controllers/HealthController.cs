using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace AudioYotoShelf.Api.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class HealthController(
    AudioYotoShelfDbContext db,
    IDistributedCache cache,
    IChapterExtractor ffmpeg) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var checks = new Dictionary<string, object>();

        // Postgres
        try
        {
            // CanConnectAsync reports an unreachable server by returning false, not by throwing.
            if (await db.Database.CanConnectAsync(ct))
                checks["postgres"] = new { status = "healthy" };
            else
                checks["postgres"] = new { status = "unhealthy", error = "Cannot connect to PostgreSQL" };
        }
        catch (Exception ex)
        {
            checks["postgres"] = new { status = "unhealthy", error = ex.Message };
        }

        // Redis
        try
        {
            await cache.SetStringAsync("health_check", "ok", new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5)
            }, ct);
            var value = await cache.GetStringAsync("health_check", ct);
            checks["redis"] = new { status = value == "ok" ? "healthy" : "unhealthy" };
        }
        catch (Exception ex)
        {
            checks["redis"] = new { status = "unhealthy", error = ex.Message };
        }

        // FFmpeg
        var ffmpegAvailable = await ffmpeg.IsFfmpegAvailableAsync(ct);
        checks["ffmpeg"] = new { status = ffmpegAvailable ? "healthy" : "unavailable" };

        var isHealthy = checks.Values
            .All(c => c.GetType().GetProperty("status")?.GetValue(c)?.ToString() != "unhealthy");

        return isHealthy ? Ok(checks) : StatusCode(503, checks);
    }
}
