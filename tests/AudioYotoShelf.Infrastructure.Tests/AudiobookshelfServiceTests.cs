using System.Net;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services.Audiobookshelf;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class AudiobookshelfServiceTests
{
    private readonly AudiobookshelfService _sut;
    private readonly FakeHttpMessageHandler _handler;

    public AudiobookshelfServiceTests()
    {
        _handler = new FakeHttpMessageHandler();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Audiobookshelf"))
            .Returns(() =>
            {
                var client = new HttpClient(_handler);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                return client;
            });

        _sut = new AudiobookshelfService(factory.Object, Mock.Of<ILogger<AudiobookshelfService>>());
    }

    // =========================================================================
    // RefreshTokenAsync — renews a stored connection via /auth/refresh
    // =========================================================================

    [Fact]
    public async Task RefreshTokenAsync_PostsToAuthRefreshAndReturnsRotatedTokens()
    {
        _handler.SetupJsonResponseFor("/auth/refresh", new AbsLoginResponse(
            new AbsUser("u1", "testuser", "user", "legacy", true, null, null,
                AccessToken: "new-access-jwt", RefreshToken: "new-refresh"),
            "lib-1"));

        var result = await _sut.RefreshTokenAsync("http://abs.local", "old-refresh");

        _handler.LastRequestUri!.Should().Contain("/auth/refresh");
        result.User.AccessToken.Should().Be("new-access-jwt");
        result.User.RefreshToken.Should().Be("new-refresh");
    }

    // =========================================================================
    // AuthorizeApiKeyAsync — resolves the user an ABS API key acts as
    // =========================================================================

    [Fact]
    public async Task AuthorizeApiKeyAsync_PostsKeyAsBearerToApiAuthorize()
    {
        _handler.SetupJsonResponseFor("/api/authorize", new AbsLoginResponse(
            new AbsUser("u1", "alice", "user", "legacy", true, null, ["lib-1"]), "lib-1"));

        await _sut.AuthorizeApiKeyAsync("http://abs.local", "api-key-jwt");

        _handler.LastRequestMethod.Should().Be(HttpMethod.Post);
        _handler.LastRequestUri.Should().Be("/api/authorize");
        _handler.LastAuthorization.Should().Be("Bearer api-key-jwt");
    }

    [Fact]
    public async Task AuthorizeApiKeyAsync_ReturnsTheUserTheKeyActsAs()
    {
        _handler.SetupJsonResponseFor("/api/authorize", new AbsLoginResponse(
            new AbsUser("u1", "alice", "user", "legacy", true, null, ["lib-1"]), "lib-1"));

        var result = await _sut.AuthorizeApiKeyAsync("http://abs.local", "api-key-jwt");

        result.User.Username.Should().Be("alice");
        result.UserDefaultLibraryId.Should().Be("lib-1");
    }

    [Fact]
    public async Task AuthorizeApiKeyAsync_RejectedKey_Throws()
    {
        _handler.SetupResponse(HttpStatusCode.Unauthorized, "Unauthorized");

        var act = () => _sut.AuthorizeApiKeyAsync("http://abs.local", "bad-key");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // ValidateTokenAsync — ABS only routes POST /api/authorize
    // =========================================================================

    [Fact]
    public async Task ValidateTokenAsync_PostsToApiAuthorize()
    {
        _handler.SetupJsonResponse(new { });

        var isValid = await _sut.ValidateTokenAsync("http://abs.local", "token");

        isValid.Should().BeTrue();
        _handler.LastRequestMethod.Should().Be(HttpMethod.Post);
        _handler.LastRequestUri.Should().Be("/api/authorize");
    }

    // =========================================================================
    // GetLibraryItemsAsync — query string construction
    // =========================================================================

    [Fact]
    public async Task GetLibraryItemsAsync_WithSearch_AppendsSearchParam()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            search: "harry potter");

        var uri = _handler.LastRequestUri!;
        uri.Should().Contain("search=harry%20potter");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_WithFilter_AppendsFilterParam()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            filter: "genres.c2NpLWZp");

        var uri = _handler.LastRequestUri!;
        uri.Should().Contain("filter=genres.c2NpLWZp");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_WithSort_AppendsSortParam()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            sort: "media.metadata.authorName");

        var uri = _handler.LastRequestUri!;
        uri.Should().Contain("sort=media.metadata.authorName");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_NullSearch_OmitsSearchParam()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            search: null);

        var uri = _handler.LastRequestUri!;
        uri.Should().NotContain("search=");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_EmptySearch_OmitsSearchParam()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            search: "  ");

        var uri = _handler.LastRequestUri!;
        uri.Should().NotContain("search=");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_NullFilter_OmitsFilterParam()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            filter: null);

        var uri = _handler.LastRequestUri!;
        uri.Should().NotContain("filter=");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_SearchWithSpecialChars_UrlEncodes()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            search: "Lord & Rings");

        var uri = _handler.LastRequestUri!;
        uri.Should().Contain("search=Lord%20%26%20Rings");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_WithCollapseSeries_AppendsBothParams()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            collapseSeries: true, search: "test");

        var uri = _handler.LastRequestUri!;
        uri.Should().Contain("collapseseries=1");
        uri.Should().Contain("search=test");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_AllParams_BuildsCorrectQueryString()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1",
            page: 2, limit: 10, sort: "media.duration",
            collapseSeries: true, search: "test", filter: "genres.abc");

        var uri = _handler.LastRequestUri!;
        uri.Should().Contain("page=2");
        uri.Should().Contain("limit=10");
        uri.Should().Contain("sort=media.duration");
        uri.Should().Contain("collapseseries=1");
        uri.Should().Contain("search=test");
        uri.Should().Contain("filter=genres.abc");
        uri.Should().Contain("minified=1");
    }

    [Fact]
    public async Task GetLibraryItemsAsync_DefaultParams_HasMinimalQueryString()
    {
        SetupItemsResponse();

        await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1");

        var uri = _handler.LastRequestUri!;
        uri.Should().Contain("page=0");
        uri.Should().Contain("limit=20");
        uri.Should().Contain("minified=1");
        uri.Should().NotContain("sort=");
        uri.Should().NotContain("search=");
        uri.Should().NotContain("filter=");
        uri.Should().NotContain("collapseseries");
    }

    // =========================================================================
    // GetLibraryItemsAsync — response deserialization
    // =========================================================================

    [Fact]
    public async Task GetLibraryItemsAsync_ValidResponse_DeserializesCorrectly()
    {
        SetupItemsResponse(total: 42);

        var result = await _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1");

        result.Should().NotBeNull();
        result.Total.Should().Be(42);
    }

    [Fact]
    public async Task GetLibraryItemsAsync_ServerError_Throws()
    {
        _handler.SetupResponse(HttpStatusCode.InternalServerError, "Server error");

        var act = () => _sut.GetLibraryItemsAsync("http://abs.local", "token", "lib-1");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // GetSeriesDetailAsync — books resolved via the items-by-series filter
    // =========================================================================

    [Fact]
    public async Task GetSeriesDetailAsync_ResolvesBooksFromItemsEndpoint()
    {
        _handler.SetupJsonResponseFor("/api/series/",
            new { id = "s1", name = "Harry Potter", description = (string?)null });
        var media = TestData.CreateAbsMedia(
            metadata: TestData.CreateAbsMetadata("Philosopher's Stone", seriesName: "Harry Potter", seriesSequence: "1"));
        _handler.SetupJsonResponseFor("/items",
            new AbsLibraryItemsResponse([TestData.CreateAbsLibraryItem("book-1", media)], 1, 500, 0));

        var result = await _sut.GetSeriesDetailAsync("http://abs.local", "token", "lib-1", "s1");

        result.Name.Should().Be("Harry Potter");
        result.Books.Should().NotBeNull();
        result.Books.Should().HaveCount(1);
        result.Books[0].Id.Should().Be("book-1");
        result.Books[0].Sequence.Should().Be("1");
    }

    [Fact]
    public async Task GetSeriesDetailAsync_FiltersBySeriesSortedBySequence()
    {
        _handler.SetupJsonResponseFor("/api/series/",
            new { id = "s1", name = "Series", description = (string?)null });
        _handler.SetupJsonResponseFor("/items", new AbsLibraryItemsResponse([], 0, 500, 0));

        await _sut.GetSeriesDetailAsync("http://abs.local", "token", "lib-1", "s1");

        var uri = _handler.LastRequestUri!; // items request runs last
        uri.Should().Contain("sort=sequence");
        uri.Should().Contain("filter=series.czE"); // base64("s1") == "czE="
    }

    [Fact]
    public async Task GetSeriesDetailAsync_NoBooks_ReturnsEmptyArrayNotNull()
    {
        _handler.SetupJsonResponseFor("/api/series/",
            new { id = "s1", name = "Empty Series", description = (string?)null });
        _handler.SetupJsonResponseFor("/items", new AbsLibraryItemsResponse([], 0, 500, 0));

        var result = await _sut.GetSeriesDetailAsync("http://abs.local", "token", "lib-1", "s1");

        result.Books.Should().NotBeNull();
        result.Books.Should().BeEmpty();
    }

    // =========================================================================
    // SearchLibraryItemsAsync — uses the dedicated search endpoint
    // =========================================================================

    [Fact]
    public async Task SearchLibraryItemsAsync_HitsSearchEndpointAndMapsBookItems()
    {
        _handler.SetupJsonResponseFor("/search", new
        {
            book = new[] { new { libraryItem = TestData.CreateAbsLibraryItem("book-1") } }
        });

        var result = await _sut.SearchLibraryItemsAsync("http://abs.local", "token", "lib-1", "narnia");

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("book-1");
        _handler.LastRequestUri!.Should().Contain("/api/libraries/lib-1/search");
        _handler.LastRequestUri!.Should().Contain("q=narnia");
    }

    [Fact]
    public async Task SearchLibraryItemsAsync_NoMatches_ReturnsEmpty()
    {
        _handler.SetupJsonResponseFor("/search", new { book = Array.Empty<object>() });

        var result = await _sut.SearchLibraryItemsAsync("http://abs.local", "token", "lib-1", "zzz");

        result.Should().BeEmpty();
    }

    // =========================================================================
    // Helper: fake HTTP handler
    // =========================================================================

    private void SetupItemsResponse(int total = 10)
    {
        var body = new AbsLibraryItemsResponse([], total, 20, 0);
        _handler.SetupJsonResponse(body);
    }

    /// <summary>
    /// Minimal HTTP handler that captures the last request and returns a canned response.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }
        public string? LastAuthorization { get; private set; }

        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _content = "{}";
        private readonly List<(string PathContains, string Json)> _routes = [];

        private static string Json<T>(T body) =>
            System.Text.Json.JsonSerializer.Serialize(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        public void SetupJsonResponse<T>(T body)
        {
            _statusCode = HttpStatusCode.OK;
            _content = Json(body);
        }

        /// <summary>Route a canned JSON body to requests whose path contains <paramref name="pathContains"/>.</summary>
        public void SetupJsonResponseFor<T>(string pathContains, T body) =>
            _routes.Add((pathContains, Json(body)));

        public void SetupResponse(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            LastRequestUri = path;
            LastRequestMethod = request.Method;
            LastAuthorization = request.Headers.Authorization?.ToString();

            var match = _routes.FirstOrDefault(r => path.Contains(r.PathContains));
            var content = match.Json ?? _content;

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
