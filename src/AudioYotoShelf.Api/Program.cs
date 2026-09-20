using AudioYotoShelf.Api.Health;
using AudioYotoShelf.Api.Hubs;
using AudioYotoShelf.Api.Middleware;
using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Services;
using AudioYotoShelf.Infrastructure.Caching;
using AudioYotoShelf.Infrastructure.Data;
using AudioYotoShelf.Infrastructure.Observability;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Services.Audiobookshelf;
using AudioYotoShelf.Infrastructure.Services.BackgroundJobs;
using AudioYotoShelf.Infrastructure.Services.IconGeneration;
using AudioYotoShelf.Infrastructure.Services.Yoto;
using FluentValidation;
using FluentValidation.AspNetCore;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Serilog ---
builder.Host.UseSerilog((context, loggerConfig) =>
{
    loggerConfig
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithThreadId()
            .WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

    var seqUrl = context.Configuration["Serilog:SeqUrl"];
    if (!string.IsNullOrEmpty(seqUrl))
        loggerConfig.WriteTo.Seq(seqUrl);
});

// The one Audiobookshelf server this deployment may talk to, if configured. Resolved here so a
// malformed URL stops the boot rather than failing every connect with a generic 500.
var configuredAbsUrl = AudiobookshelfServer.ResolveConfiguredUrl(
    builder.Configuration[AudiobookshelfServer.UrlConfigKey]);
_ = AudiobookshelfServer.ResolveConfiguredUrl(
    builder.Configuration[AudiobookshelfServer.PublicUrlConfigKey], AudiobookshelfServer.PublicUrlConfigKey);

// --- Database ---
builder.Services.AddDbContext<AudioYotoShelfDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"), npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(AudioYotoShelfDbContext).Assembly.FullName);
            npgsql.EnableRetryOnFailure(3);
        }));

// --- Redis ---
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
    options.InstanceName = "AudioYotoShelf:";
});

// --- Hangfire ---
builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(builder.Configuration.GetConnectionString("Postgres"))));
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
    options.Queues = ["transfers", "icons", "default"];
});

// --- HttpClients ---
builder.Services.AddHttpClient("Audiobookshelf", client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromMinutes(10);
});
// Single sign-on has to read Audiobookshelf's redirect (it is the authorization URL), not follow it,
// and must never let a cookie jar be shared between people.
builder.Services.AddHttpClient("AudiobookshelfSso", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });
builder.Services.AddHttpClient("Yoto", client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient("YotoAuth", client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("YotoUpload", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Presigned upload hosts drop idle keep-alive connections; retiring pooled
    // connections quickly avoids reusing a stale socket (manifests as "broken pipe").
    PooledConnectionLifetime = TimeSpan.FromSeconds(30),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
});
builder.Services.AddHttpClient("Gemini", client =>
{
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromMinutes(2);
});

// --- Services (DI) ---
builder.Services.AddScoped<IAudiobookshelfService, AudiobookshelfService>();
builder.Services.AddScoped<IYotoService, YotoService>();
builder.Services.AddScoped<IAgeSuggestionService, AgeSuggestionService>();

// Card capacity (config-driven limits; stateless calculators)
var cardLimits = builder.Configuration.GetSection(AudioYotoShelf.Core.Configuration.YotoCardLimits.SectionName)
    .Get<AudioYotoShelf.Core.Configuration.YotoCardLimits>() ?? new AudioYotoShelf.Core.Configuration.YotoCardLimits();
builder.Services.AddSingleton(cardLimits);
builder.Services.AddSingleton<ITrackPlanner, TrackPlanner>();
builder.Services.AddSingleton<ICardCapacityCalculator, CardCapacityCalculator>();
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddScoped<IChapterExtractor, FfmpegChapterExtractor>();
builder.Services.AddScoped<GeminiIconGenerationService>();
builder.Services.AddScoped<IIconGenerationService, RateLimitedIconService>();
builder.Services.AddScoped<ITransferOrchestrator, TransferOrchestrator>();
builder.Services.AddScoped<IPlaylistService, PlaylistService>();
builder.Services.AddScoped<IPlaylistTransferOrchestrator, PlaylistTransferOrchestrator>();
builder.Services.AddSingleton<ICacheService, RedisCacheService>();
builder.Services.AddSingleton<ISsoFlowStore, DistributedCacheSsoFlowStore>();
builder.Services.AddTransferJobs();
builder.Services.AddScoped<ITransferProgressNotifier, AudioYotoShelf.Api.Hubs.SignalRTransferProgressNotifier>();

// --- FluentValidation ---
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddFluentValidationAutoValidation();

// --- SignalR with Redis backplane ---
// Serialize enums as names so the Vue client receives "UploadingToYoto" instead of an int.
var signalRBuilder = builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConnectionString))
{
    signalRBuilder.AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = new StackExchange.Redis.RedisChannel(
                    "AudioYotoShelf", StackExchange.Redis.RedisChannel.PatternMode.Literal);
    });
}

// --- Observability ---
builder.Services.AddSingleton<TransferMetrics>();

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck<FfmpegHealthCheck>("ffmpeg", tags: ["ready"]);

// OpenTelemetry metrics: ASP.NET request rate/latency/5xx, outbound HTTP (ABS/Yoto/Gemini)
// latency+errors, runtime/GC stats, and our transfer counters — scraped at /metrics (Prometheus).
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("AudioYotoShelf"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(TransferMetrics.MeterName)
        .AddPrometheusExporter());

// --- AuthN/AuthZ ---
// Session lives in an http-only cookie issued at ABS login, so the credential is never readable by
// JS, never in a URL, and never in request logs. SameSite=Lax still lets the cookie ride the Yoto
// OAuth top-level redirect back to /api/auth/yoto/callback while blocking cross-site POST (CSRF).
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ays_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Secure on https, works on local http
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        // This is an API/SPA: answer with status codes instead of redirecting to a login page.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

// Authenticated by default; opt out per-endpoint with [AllowAnonymous] (login, OAuth callback, health).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// --- API ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// --- CORS for Vue dev server ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("Development", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
    });
});

var app = builder.Build();

// --- Middleware pipeline ---
app.UseGlobalExceptionHandling();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous(); // exempt from the global authenticated-by-default policy
    app.UseCors("Development");
}

app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// NOTE: the Hangfire dashboard is left unauthenticated as before (it has no UI login). It can expose
// job arguments — restrict it at the network layer or add a dashboard auth filter as a follow-up.
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "AudioYotoShelf Jobs"
}).AllowAnonymous();

app.MapControllers();
app.MapHub<TransferHub>("/hubs/transfer");

// Liveness = process is up (no dependency checks); readiness = dependencies reachable. Both
// anonymous so orchestrators/uptime monitors can probe them.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

// Prometheus scrape endpoint (/metrics). Like /hangfire, restrict this at the network layer —
// it exposes operational metrics, not user data.
app.MapPrometheusScrapingEndpoint().AllowAnonymous();

// The SPA shell must load for anonymous visitors so they can reach the login/setup screen.
app.MapFallbackToFile("index.html").AllowAnonymous();

// --- Database migration on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AudioYotoShelfDbContext>();
    await db.Database.MigrateAsync();

    if (configuredAbsUrl is not null)
    {
        var absLockLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await AbsServerLock.RepointConnectionsAsync(db, configuredAbsUrl, absLockLogger);
    }

    var ffmpeg = scope.ServiceProvider.GetRequiredService<IChapterExtractor>();
    var ffmpegAvailable = await ffmpeg.IsFfmpegAvailableAsync();
    Log.Information("FFmpeg available: {Available}", ffmpegAvailable);
}

Log.Information("AudioYotoShelf started. Environment: {Environment}", app.Environment.EnvironmentName);
await app.RunAsync();

// Required for integration test WebApplicationFactory
public partial class Program;
