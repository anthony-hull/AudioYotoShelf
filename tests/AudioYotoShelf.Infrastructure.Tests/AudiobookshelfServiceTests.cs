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
    private readonly FakeHttpMessageHandler _ssoHandler;

    public AudiobookshelfServiceTests()
    {
        _handler = new FakeHttpMessageHandler();
        _ssoHandler = new FakeHttpMessageHandler();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Audiobookshelf"))
            .Returns(() =>
            {
                var client = new HttpClient(_handler);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                return client;
            });

        // The SSO calls need a client that does not follow redirects, so they use their own named
        // client. Routing them to a separate handler here proves the service asks for it.
        factory.Setup(f => f.CreateClient("AudiobookshelfSso"))
            .Returns(() => new HttpClient(_ssoHandler));

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
    // =========================================================================
    // StartSsoAsync — step 1 of the OIDC API flow: get ABS's authorization URL and session
    // =========================================================================

    private const string AbsAuthorizeLocation =
        "https://auth.example/application/o/authorize/?client_id=abc&state=s";
    private const string OurCallback = "https://yoto.example/api/auth/abs/sso/callback";

    [Fact]
    public async Task StartSsoAsync_AsksAbsForAPkceCodeFlowToOurCallback()
    {
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation);

        await _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + _ssoHandler.LastRequestUri).Query);
        _ssoHandler.LastRequestMethod.Should().Be(HttpMethod.Get);
        _ssoHandler.LastRequestUri.Should().StartWith("/auth/openid?");
        query["response_type"].Should().Be("code");
        query["redirect_uri"].Should().Be(OurCallback);
        query["code_challenge_method"].Should().Be("S256");
    }

    [Fact]
    public async Task StartSsoAsync_ChallengeIsTheS256OfTheVerifierWeKeep()
    {
        // If these two disagree, Authentik rejects the code exchange at the very last step.
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation);

        var start = await _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + _ssoHandler.LastRequestUri).Query);
        var expectedChallenge = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(start.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        query["code_challenge"].Should().Be(expectedChallenge);
        query["state"].Should().Be(start.State);
    }

    [Fact]
    public async Task StartSsoAsync_VerifierIsWithinTheLengthPkceAllows()
    {
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation);

        var start = await _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        start.CodeVerifier.Length.Should().BeInRange(43, 128);
        start.CodeVerifier.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public async Task StartSsoAsync_ReturnsAbsLocationWithoutFollowingIt()
    {
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation);

        var start = await _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        start.AuthorizationUrl.Should().Be(AbsAuthorizeLocation);
    }

    [Fact]
    public async Task StartSsoAsync_ReturnsTheCookiesAbsSetWithoutTheirAttributes()
    {
        // The callback (step 4) fails with "No session" unless it presents these.
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation,
            "connect.sid=s%3Aabc.sig; Path=/; HttpOnly",
            "auth_method=openid-mobile; Max-Age=315360000; Path=/; HttpOnly");

        var start = await _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        start.Cookies.Should().Equal(new Dictionary<string, string>
        {
            ["connect.sid"] = "s%3Aabc.sig",
            ["auth_method"] = "openid-mobile",
        });
    }

    [Fact]
    public async Task StartSsoAsync_WithPublicUrl_MakesAbsBuildItsCallbackForThatHost()
    {
        // ABS derives the URL Authentik sends the browser back to from the request's own Host.
        // Called by its internal name that is http://audiobookshelf/..., which no browser can reach.
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation);

        await _sut.StartSsoAsync("http://abs.local", "https://audiobooks.example", OurCallback);

        _ssoHandler.LastHost.Should().Be("audiobooks.example");
        _ssoHandler.LastForwardedProto.Should().Be("https");
    }

    [Fact]
    public async Task StartSsoAsync_WithoutPublicUrl_DoesNotOverrideHost()
    {
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation);

        await _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        _ssoHandler.LastHost.Should().BeNull();
        _ssoHandler.LastForwardedProto.Should().BeNull();
    }

    [Fact]
    public async Task StartSsoAsync_EveryCallGetsAFreshStateAndVerifier()
    {
        _ssoHandler.SetupRedirect(AbsAuthorizeLocation);

        var first = await _sut.StartSsoAsync("http://abs.local", null, OurCallback);
        var second = await _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        first.State.Should().NotBe(second.State);
        first.CodeVerifier.Should().NotBe(second.CodeVerifier);
    }

    [Fact]
    public async Task StartSsoAsync_AbsRefusesTheRedirectUri_ThrowsSayingSo()
    {
        _ssoHandler.SetupResponse(HttpStatusCode.BadRequest, "Invalid redirect_uri");

        var act = () => _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("Invalid redirect_uri");
    }

    [Fact]
    public async Task StartSsoAsync_AbsDoesNotRedirect_ThrowsRatherThanReturningNothing()
    {
        // e.g. OpenID is switched off in ABS and the route answers 200/404 instead of a redirect.
        _ssoHandler.SetupResponse(HttpStatusCode.OK, "ok");

        var act = () => _sut.StartSsoAsync("http://abs.local", null, OurCallback);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // CompleteSsoAsync — step 4: trade the code for the person's own ABS tokens
    // =========================================================================

    private static readonly IReadOnlyDictionary<string, string> AbsSessionCookies =
        new Dictionary<string, string> { ["connect.sid"] = "s%3Aabc.sig", ["auth_method"] = "openid-mobile" };

    private static AbsLoginResponse SsoLoginResponse() => new(
        new AbsUser("u1", "alice", "user", "legacy", true, null, ["lib-1"],
            AccessToken: "access-jwt", RefreshToken: "refresh-token"),
        "lib-1");

    [Fact]
    public async Task CompleteSsoAsync_SendsTheSessionCookiesFromStepOne()
    {
        // Without them ABS answers 400 "No session" (Auth.js checks req.session[strategyKey]).
        _ssoHandler.SetupJsonResponseFor("/auth/openid/callback", SsoLoginResponse());

        await _sut.CompleteSsoAsync("http://abs.local", "the-code", "the-state", "the-verifier", AbsSessionCookies);

        _ssoHandler.LastCookie.Should().Be("connect.sid=s%3Aabc.sig; auth_method=openid-mobile");
    }

    [Fact]
    public async Task CompleteSsoAsync_PassesCodeStateAndVerifierToAbs()
    {
        _ssoHandler.SetupJsonResponseFor("/auth/openid/callback", SsoLoginResponse());

        await _sut.CompleteSsoAsync("http://abs.local", "the code+1", "the-state", "the-verifier", AbsSessionCookies);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + _ssoHandler.LastRequestUri).Query);
        _ssoHandler.LastRequestMethod.Should().Be(HttpMethod.Get);
        _ssoHandler.LastRequestUri.Should().StartWith("/auth/openid/callback?");
        query["code"].Should().Be("the code+1");
        query["state"].Should().Be("the-state");
        query["code_verifier"].Should().Be("the-verifier");
    }

    [Fact]
    public async Task CompleteSsoAsync_ReturnsTheUsersOwnTokens()
    {
        _ssoHandler.SetupJsonResponseFor("/auth/openid/callback", SsoLoginResponse());

        var result = await _sut.CompleteSsoAsync("http://abs.local", "c", "s", "v", AbsSessionCookies);

        result.User.Username.Should().Be("alice");
        result.User.AccessToken.Should().Be("access-jwt");
        result.User.RefreshToken.Should().Be("refresh-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "No session")]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized")]
    public async Task CompleteSsoAsync_AbsError_ThrowsRatherThanStoringAHalfConnection(
        HttpStatusCode status, string body)
    {
        _ssoHandler.SetupResponse(status, body);

        var act = () => _sut.CompleteSsoAsync("http://abs.local", "c", "s", "v", AbsSessionCookies);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain(body);
    }

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastHost { get; private set; }
        public string? LastForwardedProto { get; private set; }
        public string? LastCookie { get; private set; }

        private string? _location;
        private string[] _setCookies = [];
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

        /// <summary>Answer every request with a 302 to <paramref name="location"/> and these Set-Cookie headers.</summary>
        public void SetupRedirect(string location, params string[] setCookies)
        {
            _statusCode = HttpStatusCode.Found;
            _location = location;
            _setCookies = setCookies;
        }

        public void SetupResponse(HttpStatusCode statusCode, string content)
        {
            _location = null;
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
            LastHost = request.Headers.Host;
            LastForwardedProto = request.Headers.TryGetValues("X-Forwarded-Proto", out var proto) ? proto.Single() : null;
            LastCookie = request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join("; ", cookie) : null;

            var match = _routes.FirstOrDefault(r => path.Contains(r.PathContains));
            var content = match.Json ?? _content;

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
            if (_location is not null)
                response.Headers.Location = new Uri(_location);
            foreach (var setCookie in _setCookies)
                response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);

            return Task.FromResult(response);
        }
    }
}
