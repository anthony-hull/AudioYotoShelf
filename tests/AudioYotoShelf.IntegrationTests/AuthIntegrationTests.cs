using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace AudioYotoShelf.IntegrationTests;

/// <summary>
/// End-to-end-ish API tests over the real pipeline (Kestrel TestServer + Postgres + Redis):
/// startup migrations, cookie auth, ownership, admin gating, health probes, and metrics.
/// </summary>
public class AuthIntegrationTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private async Task<HttpClient> ConnectAsync(string username, string baseUrl = IntegrationTestFactory.AdminAbsUrl)
    {
        factory.Abs.Username = username;
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/auth/abs/connect", new { baseUrl, username = "x", password = "y" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    /// <summary>A client for an app with its Audiobookshelf server fixed by configuration.</summary>
    private HttpClient CreateClientWithConfiguredServer() =>
        factory.WithWebHostBuilder(b => b.UseSetting("Audiobookshelf:Url", IntegrationTestFactory.AdminAbsUrl))
            .CreateClient();

    [Fact]
    public async Task ApiKeyConnect_WithConfiguredServer_ConnectsToThatServer()
    {
        factory.Abs.Username = "keyholder";
        var client = CreateClientWithConfiguredServer();

        var resp = await client.PostAsJsonAsync("/api/auth/abs/connect", new { apiKey = "abs-api-key" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Abs.LastBaseUrl.Should().Be(IntegrationTestFactory.AdminAbsUrl);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        status.GetProperty("username").GetString().Should().Be("keyholder");
    }

    [Fact]
    public async Task ApiKeyConnect_AdminUserOnConfiguredServer_GetsAdminSession()
    {
        // Admin promotion keys off the server actually used, so omitting the URL in favour of the
        // configured one must still count as the trusted admin server.
        factory.Abs.Username = "adminuser";
        var client = CreateClientWithConfiguredServer();
        await client.PostAsJsonAsync("/api/auth/abs/connect", new { apiKey = "abs-api-key" });

        var resp = await client.GetAsync("/api/admin/overview");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConnectOptions_AnonymousCaller_SeesServerUrlLocked()
    {
        var json = await CreateClientWithConfiguredServer()
            .GetFromJsonAsync<JsonElement>("/api/auth/abs/options");

        json.GetProperty("isServerUrlLocked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task HealthReady_Returns200_AgainstRealDependencies()
    {
        var resp = await factory.CreateClient().GetAsync("/health/ready");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthLive_Returns200()
    {
        var resp = await factory.CreateClient().GetAsync("/health/live");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metrics_Returns200()
    {
        var resp = await factory.CreateClient().GetAsync("/metrics");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Libraries_WithoutSession_Returns401()
    {
        var resp = await factory.CreateClient().GetAsync("/api/libraries");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Connect_IssuesSession_AndStatusReflectsConnection()
    {
        // Proves migrations applied (writes UserConnection + LoginEvent), the cookie is issued,
        // and the session is honored on a follow-up request.
        var client = await ConnectAsync("alice");

        var status = await client.GetAsync("/api/auth/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await status.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("absConnected").GetBoolean().Should().BeTrue();
        json.GetProperty("username").GetString().Should().Be("alice");
    }

    [Fact]
    public async Task Libraries_WithSession_Returns200()
    {
        var client = await ConnectAsync("bookworm");
        var resp = await client.GetAsync("/api/libraries");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_ForbiddenForNonAdminUser()
    {
        // Authenticated, but not in Admin:Usernames -> no admin role -> 403.
        var client = await ConnectAsync("regularuser");
        var resp = await client.GetAsync("/api/admin/overview");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_AllowedForAdminUser_AndLoginIsTracked()
    {
        // adminuser is allow-listed and logs in against the trusted ABS URL -> admin session.
        var client = await ConnectAsync("adminuser");
        var resp = await client.GetAsync("/api/admin/overview");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("totalUsers").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        json.GetProperty("totalLogins").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }
}
