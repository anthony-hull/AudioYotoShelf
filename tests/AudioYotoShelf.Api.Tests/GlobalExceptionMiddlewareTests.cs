using System.Text.Json;
using AudioYotoShelf.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class GlobalExceptionMiddlewareTests
{
    private readonly Mock<ILogger<GlobalExceptionMiddleware>> _logger = new();

    private GlobalExceptionMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, _logger.Object);

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/api/test";
        context.Request.Method = "GET";
        return context;
    }

    private static async Task<ProblemDetails?> ReadProblemDetails(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<ProblemDetails>(
            context.Response.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    // =========================================================================
    // Status code mapping
    // =========================================================================

    [Fact]
    public async Task InvalidOperationException_Returns400()
    {
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("Bad input"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(400);
        var problem = await ReadProblemDetails(context);
        problem!.Title.Should().Be("Invalid Operation");
        problem.Detail.Should().Be("Bad input");
    }

    [Fact]
    public async Task UnauthorizedAccessException_Returns401()
    {
        var middleware = CreateMiddleware(_ => throw new UnauthorizedAccessException("No auth"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
        var problem = await ReadProblemDetails(context);
        problem!.Title.Should().Be("Unauthorized");
    }

    [Fact]
    public async Task KeyNotFoundException_Returns404()
    {
        var middleware = CreateMiddleware(_ => throw new KeyNotFoundException("Not found"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task TimeoutException_Returns504()
    {
        var middleware = CreateMiddleware(_ => throw new TimeoutException("Timed out"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(504);
    }

    [Fact]
    public async Task HttpRequestException_Returns502()
    {
        var middleware = CreateMiddleware(_ => throw new HttpRequestException("External failure"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(502);
        var problem = await ReadProblemDetails(context);
        problem!.Title.Should().Be("External Service Error");
    }

    [Fact]
    public async Task OperationCanceledException_Returns400()
    {
        var middleware = CreateMiddleware(_ => throw new OperationCanceledException());
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task UnhandledException_Returns500()
    {
        var middleware = CreateMiddleware(_ => throw new NullReferenceException("Oops"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(500);
        var problem = await ReadProblemDetails(context);
        problem!.Title.Should().Be("Internal Server Error");
        problem.Detail.Should().Be("An unexpected error occurred");
    }

    // =========================================================================
    // Response format
    // =========================================================================

    [Fact]
    public async Task AllExceptions_ReturnProblemJson_ContentType()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("boom"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task AllExceptions_IncludeTraceId()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("boom"));
        var context = CreateHttpContext();
        context.TraceIdentifier = "trace-abc-123";

        await middleware.InvokeAsync(context);

        var problem = await ReadProblemDetails(context);
        problem!.Extensions["traceId"]!.ToString().Should().Be("trace-abc-123");
    }

    [Fact]
    public async Task AllExceptions_IncludeInstancePath()
    {
        var middleware = CreateMiddleware(_ => throw new Exception("boom"));
        var context = CreateHttpContext();
        context.Request.Path = "/api/transfers/123";

        await middleware.InvokeAsync(context);

        var problem = await ReadProblemDetails(context);
        problem!.Instance.Should().Be("/api/transfers/123");
    }

    // =========================================================================
    // Logging behavior
    // =========================================================================

    [Fact]
    public async Task InternalServerError_LogsError()
    {
        var middleware = CreateMiddleware(_ => throw new NullReferenceException("Oops"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        _logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ClientError_LogsWarning()
    {
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("bad"));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        _logger.Verify(x => x.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // =========================================================================
    // No exception path
    // =========================================================================

    [Fact]
    public async Task NoException_PassesThrough()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(200);
    }

    // =========================================================================
    // Mutation-testing additions — the full RFC 7807 contract for each exception type
    // =========================================================================

    [Theory]
    [MemberData(nameof(ExceptionContracts))]
    public async Task EachExceptionType_MapsToItsStatusTitleAndDetail(
        Exception thrown, int expectedStatus, string expectedTitle, string expectedDetail)
    {
        var middleware = CreateMiddleware(_ => throw thrown);
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(expectedStatus);
        var problem = await ReadProblemDetails(context);
        problem!.Status.Should().Be(expectedStatus);
        problem.Title.Should().Be(expectedTitle);
        problem.Detail.Should().Be(expectedDetail);
        problem.Type.Should().Be($"https://httpstatuses.com/{expectedStatus}");
    }

    public static TheoryData<Exception, int, string, string> ExceptionContracts => new()
    {
        { new InvalidOperationException("bad"), 400, "Invalid Operation", "bad" },
        { new UnauthorizedAccessException("no"), 401, "Unauthorized", "no" },
        { new KeyNotFoundException("gone"), 404, "Not Found", "gone" },
        { new TimeoutException("slow"), 504, "Timeout", "slow" },
        { new OperationCanceledException("ignored"), 400, "Cancelled", "The operation was cancelled" },
        { new HttpRequestException("upstream"), 502, "External Service Error", "upstream" },
        { new NullReferenceException("secret internals"), 500, "Internal Server Error", "An unexpected error occurred" },
    };

    [Fact]
    public async Task ValidationException_Returns422_ListingEveryFailedProperty()
    {
        var failures = new[]
        {
            new FluentValidation.Results.ValidationFailure("Name", "Name is required"),
            new FluentValidation.Results.ValidationFailure("Age", "Age must be positive"),
        };
        var middleware = CreateMiddleware(_ => throw new FluentValidation.ValidationException(failures));
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(422);
        var problem = await ReadProblemDetails(context);
        problem!.Title.Should().Be("Validation Error");
        problem.Detail.Should().Be("Name: Name is required; Age: Age must be positive");
    }

    [Fact]
    public async Task Response_IsCompactCamelCaseJson_CarryingTheTraceId()
    {
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("bad"));
        var context = CreateHttpContext();
        context.TraceIdentifier = "trace-abc-123";

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        raw.Should().NotContain("\n").And.NotContain("  ");
        using var json = JsonDocument.Parse(raw);
        json.RootElement.GetProperty("traceId").GetString().Should().Be("trace-abc-123");
        json.RootElement.TryGetProperty("title", out _).Should().BeTrue();
    }

    [Fact]
    public async Task UseGlobalExceptionHandling_CatchesExceptionsFromTheRestOfThePipeline()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseGlobalExceptionHandling();
        app.Run(_ => throw new KeyNotFoundException("missing"));
        var context = CreateHttpContext();

        await app.Build()(context);

        context.Response.StatusCode.Should().Be(404);
    }
}
