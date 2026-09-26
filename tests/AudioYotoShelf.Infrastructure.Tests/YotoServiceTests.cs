using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Infrastructure.Services.Yoto;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

/// <summary>
/// The contract with Yoto's public API: exact method, URL, headers and body of every request, and
/// how each response shape is mapped. A wrong path or header silently breaks the whole app, so
/// these assert the values rather than "a request was made".
/// </summary>
public class YotoServiceTests
{
    private const string Token = "access-token";
    private const string ApiBase = "https://api.yotoplay.com";
    private const string AuthBase = "https://login.yotoplay.com";
    private const string UploadUrl = "https://upload.example.com/presigned";
    private const string PollDoneJson = """{"transcode":{"transcodedSha256":"sha-1","status":"complete"}}""";
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly RecordingHandler _handler = new();
    private readonly List<string> _clientNames = [];

    // =========================================================================
    // Configuration and the authorization URL
    // =========================================================================

    [Fact]
    public void GetAuthorizationUrl_UsesTheRealYotoEndpointsByDefault()
    {
        var url = new Uri(CreateSut().GetAuthorizationUrl("https://app.example/callback", "state-1"));

        url.GetLeftPart(UriPartial.Path).Should().Be($"{AuthBase}/authorize");
        var query = HttpUtility.ParseQueryString(url.Query);
        query.AllKeys.Should().Equal("response_type", "client_id", "redirect_uri", "scope", "audience", "state");
        query["response_type"].Should().Be("code");
        query["client_id"].Should().Be("client-1");
        query["redirect_uri"].Should().Be("https://app.example/callback");
        // Yoto grants only user:account:view to a client that does not ask, and refuses uploads without
        // user:content:manage, so the content and icon scopes are requested on purpose.
        query["scope"].Should().Be(
            "profile offline_access openid user:content:manage user:content:view user:icons:manage");
        query["audience"].Should().Be(ApiBase);
        query["state"].Should().Be("state-1");
    }

    [Fact]
    public void GetAuthorizationUrl_UsesTheConfiguredApiBaseAsTheAudience()
    {
        var url = new Uri(CreateSut(apiBase: "http://localhost:8888").GetAuthorizationUrl("https://app/cb", "s"));

        HttpUtility.ParseQueryString(url.Query)["audience"].Should().Be("http://localhost:8888");
    }

    [Fact]
    public void GetAuthorizationUrl_WithoutAClientId_ThrowsNamingTheSetting()
    {
        var act = () => CreateSut(clientId: null).GetAuthorizationUrl("https://app/cb", "s");

        act.Should().Throw<InvalidOperationException>().WithMessage("Yoto:ClientId not configured");
    }

    // =========================================================================
    // OAuth token exchange and refresh
    // =========================================================================

    [Fact]
    public async Task ExchangeAuthCodeAsync_PostsTheCodeGrantToTheAuthServerAndReturnsTheTokens()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt","token_type":"Bearer","expires_in":86400}""");

        var result = await CreateSut(clientSecret: "shh").ExchangeAuthCodeAsync("code-1", "https://app/cb");

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be($"{AuthBase}/oauth/token");
        request.ContentType.Should().Be("application/x-www-form-urlencoded");
        request.Form.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "code-1",
            ["redirect_uri"] = "https://app/cb",
            ["client_id"] = "client-1",
            ["client_secret"] = "shh",
        });
        _clientNames.Should().Equal("YotoAuth");
        result.Should().Be(new YotoTokenResponse("at", "rt", "Bearer", 86400));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ExchangeAuthCodeAsync_WithoutAClientSecret_OmitsIt(string? clientSecret)
    {
        _handler.Enqueue(HttpStatusCode.OK, TokenJson);

        await CreateSut(clientSecret: clientSecret).ExchangeAuthCodeAsync("code-1", "https://app/cb");

        _handler.Requests.Single().Form.Keys.Should().BeEquivalentTo("grant_type", "code", "redirect_uri", "client_id");
    }

    [Fact]
    public async Task ExchangeAuthCodeAsync_UsesTheConfiguredAuthBase()
    {
        _handler.Enqueue(HttpStatusCode.OK, TokenJson);

        await CreateSut(authBase: "http://localhost:9999").ExchangeAuthCodeAsync("c", "r");

        _handler.Requests.Single().Uri.Should().Be("http://localhost:9999/oauth/token");
    }

    [Fact]
    public async Task ExchangeAuthCodeAsync_RejectedCode_ThrowsInsteadOfReturningTheErrorBody()
    {
        _handler.Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var act = () => CreateSut().ExchangeAuthCodeAsync("bad", "https://app/cb");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ExchangeAuthCodeAsync_EmptyTokenBody_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, "null");

        var act = () => CreateSut().ExchangeAuthCodeAsync("c", "r");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Failed to deserialize token response");
    }

    [Fact]
    public async Task RefreshTokenAsync_PostsTheRefreshGrantToTheAuthServerAndReturnsTheTokens()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"access_token":"new-at","refresh_token":"new-rt","token_type":"Bearer","expires_in":3600}""");

        var result = await CreateSut(clientSecret: "shh").RefreshTokenAsync("old-rt");

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be($"{AuthBase}/oauth/token");
        request.Form.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "old-rt",
            ["client_id"] = "client-1",
            ["client_secret"] = "shh",
        });
        _clientNames.Should().Equal("YotoAuth");
        result.Should().Be(new YotoTokenResponse("new-at", "new-rt", "Bearer", 3600));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RefreshTokenAsync_WithoutAClientSecret_OmitsIt(string? clientSecret)
    {
        _handler.Enqueue(HttpStatusCode.OK, TokenJson);

        await CreateSut(clientSecret: clientSecret).RefreshTokenAsync("rt");

        _handler.Requests.Single().Form.Keys.Should().BeEquivalentTo("grant_type", "refresh_token", "client_id");
    }

    [Fact]
    public async Task RefreshTokenAsync_RejectedToken_Throws()
    {
        _handler.Enqueue(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""");

        var act = () => CreateSut().RefreshTokenAsync("revoked");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RefreshTokenAsync_EmptyTokenBody_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, "null");

        var act = () => CreateSut().RefreshTokenAsync("rt");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Failed to refresh Yoto token");
    }

    // =========================================================================
    // GetUserCardsAsync — probes the candidate "list my cards" endpoints in order
    // =========================================================================

    [Fact]
    public async Task GetUserCardsAsync_ReadsTheFirstEndpointAndStopsThere()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"cards":[{"cardId":"c1","title":"Story"},{"cardId":"c2"}]}""");

        var cards = await CreateSut().GetUserCardsAsync(Token);

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be($"{ApiBase}/content/mine");
        request.Authorization.Should().Be($"Bearer {Token}");
        _clientNames.Should().Equal("Yoto");
        cards.Select(c => (c.CardId, c.Title)).Should().Equal(("c1", "Story"), ("c2", null));
    }

    [Fact]
    public async Task GetUserCardsAsync_FallsBackToTheFamilyLibraryWhenTheFirstEndpointIsRefused()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, "{}");
        _handler.Enqueue(HttpStatusCode.OK, """{"cards":[{"cardId":"family-1"}]}""");

        var cards = await CreateSut().GetUserCardsAsync(Token);

        _handler.Requests.Select(r => r.Uri).Should().Equal(
            $"{ApiBase}/content/mine",
            $"{ApiBase}/card/family/library/mine?showDeleted=false");
        _handler.Requests.Should().OnlyContain(r => r.Authorization == $"Bearer {Token}");
        cards.Single().CardId.Should().Be("family-1");
    }

    [Fact]
    public async Task GetUserCardsAsync_WhenEveryEndpointIsRefused_ThrowsForbidden()
    {
        _handler.RespondAlways(HttpStatusCode.Forbidden, "{}");

        var act = () => CreateSut().GetUserCardsAsync(Token);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>().WithMessage("No Yoto card-list endpoint succeeded");
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"cards":null}""")]
    public async Task GetUserCardsAsync_ABodyWithNoCards_ReturnsAnEmptyList(string json)
    {
        _handler.Enqueue(HttpStatusCode.OK, json);

        (await CreateSut().GetUserCardsAsync(Token)).Should().BeEmpty();
    }

    // =========================================================================
    // GetCardContentAsync — the card may be nested under "card" or sit at the root
    // =========================================================================

    private const string NestedCardBody = """
        {"title":"Bedtime","content":{"chapters":[{"key":"01","title":"Ch 1","tracks":[{"key":"01","title":"T1","trackUrl":"yoto:#abc","format":"aac","type":"audio","duration":12.5,"fileSize":1000,"channels":"stereo","display":{"icon16x16":"yoto:#icon"}}],"display":null}],"config":{"allowSkip":true},"playbackType":"linear","version":"1"},"metadata":{"author":"Ann","minAge":3,"maxAge":7,"genre":["fiction"],"cover":{"imageL":"https://img.example/c.jpg"}}}
        """;

    [Theory]
    [InlineData("""{"card":""" + NestedCardBody + "}")]
    [InlineData(NestedCardBody)]
    public async Task GetCardContentAsync_MapsTheCardWhetherNestedOrAtTheRoot(string json)
    {
        _handler.Enqueue(HttpStatusCode.OK, json);

        var card = await CreateSut().GetCardContentAsync(Token, "card-9");

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be($"{ApiBase}/content/card-9");
        request.Authorization.Should().Be($"Bearer {Token}");
        card.CardId.Should().Be("card-9");
        card.Title.Should().Be("Bedtime");
        card.Content!.PlaybackType.Should().Be("linear");
        card.Content.Version.Should().Be("1");
        card.Content.Config!.AllowSkip.Should().BeTrue();
        var chapter = card.Content.Chapters.Single();
        (chapter.Key, chapter.Title).Should().Be(("01", "Ch 1"));
        var track = chapter.Tracks.Single();
        track.TrackUrl.Should().Be("yoto:#abc");
        track.Duration.Should().Be(12.5);
        track.FileSize.Should().Be(1000);
        track.Format.Should().Be("aac");
        track.Display!.Icon16X16.Should().Be("yoto:#icon");
        card.Metadata!.Author.Should().Be("Ann");
        card.Metadata.MinAge.Should().Be(3);
        card.Metadata.MaxAge.Should().Be(7);
        card.Metadata.Cover!.ImageL.Should().Be("https://img.example/c.jpg");
    }

    [Fact]
    public async Task GetCardContentAsync_ACardKeyThatIsNotAnObject_FallsBackToTheRoot()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"card":"oops","title":"Root title"}""");

        var card = await CreateSut().GetCardContentAsync(Token, "c1");

        card.Title.Should().Be("Root title");
    }

    [Fact]
    public async Task GetCardContentAsync_FieldsOfTheWrongShape_AreIgnoredRatherThanThrowing()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"card":{"content":"nope","metadata":"oops","title":42}}""");

        var card = await CreateSut().GetCardContentAsync(Token, "c1");

        card.Content.Should().BeNull();
        card.Metadata.Should().BeNull();
        card.Title.Should().BeNull();
    }

    [Fact]
    public async Task GetCardContentAsync_AMissingCard_Throws()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, "{}");

        var act = () => CreateSut().GetCardContentAsync(Token, "gone");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // CreateOrUpdateCardAsync
    // =========================================================================

    [Fact]
    public async Task CreateOrUpdateCardAsync_PostsTheCardAsCamelCaseJsonAndReturnsTheNewId()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"cardId":"new-1"}""");

        var id = await CreateSut().CreateOrUpdateCardAsync(Token, SampleContent, SampleMetadata, title: "My card");

        id.Should().Be("new-1");
        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be($"{ApiBase}/content");
        request.Authorization.Should().Be($"Bearer {Token}");
        request.ContentType.Should().StartWith("application/json");
        using var body = JsonDocument.Parse(request.BodyText);
        body.RootElement.GetProperty("cardId").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("title").GetString().Should().Be("My card");
        body.RootElement.GetProperty("content").GetProperty("playbackType").GetString().Should().Be("linear");
        body.RootElement.GetProperty("content").GetProperty("chapters")[0]
            .GetProperty("tracks")[0].GetProperty("trackUrl").GetString().Should().Be("yoto:#sha");
        body.RootElement.GetProperty("metadata").GetProperty("author").GetString().Should().Be("Ann");
    }

    [Fact]
    public async Task CreateOrUpdateCardAsync_UpdatingAnExistingCard_SendsItsId()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"cardId":"card-7"}""");

        var id = await CreateSut().CreateOrUpdateCardAsync(Token, SampleContent, SampleMetadata, existingCardId: "card-7");

        id.Should().Be("card-7");
        using var body = JsonDocument.Parse(_handler.Requests.Single().BodyText);
        body.RootElement.GetProperty("cardId").GetString().Should().Be("card-7");
    }

    [Theory]
    [InlineData("""{"cardId":"c1"}""", "c1")]
    [InlineData("""{"id":"c2"}""", "c2")]
    [InlineData("""{"contentId":"c3"}""", "c3")]
    [InlineData("""{"card":{"cardId":"c4"}}""", "c4")]
    [InlineData("""{"cardId":"first","id":"second","contentId":"third"}""", "first")]
    [InlineData("""{"id":"second","contentId":"third"}""", "second")]
    [InlineData("""{"id":5,"contentId":"c9"}""", "c9")]
    [InlineData("""{"a":{"x":1},"b":{"id":"later"}}""", "later")]
    [InlineData("""{"a":{"b":{"c":{"id":"deep"}}}}""", "deep")]
    public async Task CreateOrUpdateCardAsync_FindsTheCardIdWhereverYotoPutsIt(string responseJson, string expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, responseJson);

        (await CreateSut().CreateOrUpdateCardAsync(Token, SampleContent, SampleMetadata)).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"a":{"b":{"c":{"d":{"id":"too-deep"}}}}}""")]
    [InlineData("""{"list":[{"id":"in-an-array"}]}""")]
    [InlineData("""{"id":5}""")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateOrUpdateCardAsync_NoRecognizableCardId_Throws(string responseJson)
    {
        _handler.Enqueue(HttpStatusCode.OK, responseJson);

        var act = () => CreateSut().CreateOrUpdateCardAsync(Token, SampleContent, SampleMetadata);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No cardId returned from Yoto");
    }

    [Fact]
    public async Task CreateOrUpdateCardAsync_ADeletedCard_IsRecreatedWithoutTheOldId()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, """{"message":"Deleted card cannot be updated"}""");
        _handler.Enqueue(HttpStatusCode.OK, """{"cardId":"fresh-1"}""");

        var id = await CreateSut().CreateOrUpdateCardAsync(
            Token, SampleContent, SampleMetadata, title: "My card", existingCardId: "old-1");

        id.Should().Be("fresh-1");
        _handler.Requests.Should().HaveCount(2);
        using var first = JsonDocument.Parse(_handler.Requests[0].BodyText);
        using var second = JsonDocument.Parse(_handler.Requests[1].BodyText);
        first.RootElement.GetProperty("cardId").GetString().Should().Be("old-1");
        second.RootElement.GetProperty("cardId").ValueKind.Should().Be(JsonValueKind.Null);
        second.RootElement.GetProperty("title").GetString().Should().Be("My card");
    }

    [Fact]
    public async Task CreateOrUpdateCardAsync_TheDeletedCardMessageIsMatchedIgnoringCase()
    {
        _handler.Enqueue(HttpStatusCode.Conflict, "the card was DELETED CARD earlier");
        _handler.Enqueue(HttpStatusCode.OK, """{"cardId":"fresh-2"}""");

        (await CreateSut().CreateOrUpdateCardAsync(Token, SampleContent, SampleMetadata, existingCardId: "old-2"))
            .Should().Be("fresh-2");
    }

    [Fact]
    public async Task CreateOrUpdateCardAsync_DeletedCardOnAFirstCreate_ThrowsWithoutRetrying()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, """{"message":"Deleted card"}""");

        var act = () => CreateSut().CreateOrUpdateCardAsync(Token, SampleContent, SampleMetadata, existingCardId: null);

        await act.Should().ThrowAsync<HttpRequestException>();
        _handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateOrUpdateCardAsync_OtherFailuresOnAnExistingCard_ThrowWithoutRetrying()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, """{"message":"boom"}""");

        var act = () => CreateSut().CreateOrUpdateCardAsync(Token, SampleContent, SampleMetadata, existingCardId: "card-1");

        await act.Should().ThrowAsync<HttpRequestException>();
        _handler.Requests.Should().ContainSingle();
    }

    // =========================================================================
    // DeleteCardAsync
    // =========================================================================

    [Fact]
    public async Task DeleteCardAsync_SendsADeleteForThatCard()
    {
        _handler.Enqueue(HttpStatusCode.OK, "{}");

        await CreateSut().DeleteCardAsync(Token, "card-3");

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Delete);
        request.Uri.Should().Be($"{ApiBase}/content/card-3");
        request.Authorization.Should().Be($"Bearer {Token}");
        _clientNames.Should().Equal("Yoto");
    }

    [Fact]
    public async Task DeleteCardAsync_ARefusedDelete_Throws()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, "{}");

        var act = () => CreateSut().DeleteCardAsync(Token, "card-3");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // GetUploadUrlAsync
    // =========================================================================

    [Fact]
    public async Task GetUploadUrlAsync_ReadsTheUploadUrlAndId()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"upload":{"uploadUrl":"https://s3.example/put","uploadId":"up-1"}}""");

        var info = await CreateSut().GetUploadUrlAsync(Token);

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be($"{ApiBase}/media/transcode/audio/uploadUrl");
        request.Authorization.Should().Be($"Bearer {Token}");
        info.Should().Be(new YotoUploadInfo("https://s3.example/put", "up-1"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task GetUploadUrlAsync_NoUploadInTheResponse_Throws(string json)
    {
        _handler.Enqueue(HttpStatusCode.OK, json);

        var act = () => CreateSut().GetUploadUrlAsync(Token);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No upload URL returned");
    }

    [Fact]
    public async Task GetUploadUrlAsync_ARefusedRequest_Throws()
    {
        _handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

        var act = () => CreateSut().GetUploadUrlAsync(Token);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // UploadAudioFileAsync — the request, the rewind, and which failures are retried
    // =========================================================================

    [Fact]
    public async Task UploadAudioFileAsync_PutsTheAudioToThePresignedUrlWithItsContentType()
    {
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new MemoryStream(Payload);

        await CreateSut().UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Put);
        request.Uri.Should().Be(UploadUrl);
        request.ContentType.Should().Be("audio/mp4");
        request.Body.Should().Equal(Payload);
        request.Authorization.Should().BeNull("the presigned URL carries its own credentials");
        _clientNames.Should().Equal("YotoUpload");
    }

    [Fact]
    public async Task UploadAudioFileAsync_SendsTheDeclaredContentLength()
    {
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new MemoryStream(Payload);

        await CreateSut().UploadAudioFileAsync(UploadUrl, stream, contentLength: 3, "audio/mp4");

        _handler.Requests.Single().ContentLength.Should().Be(3);
    }

    [Fact]
    public async Task UploadAudioFileAsync_WithNoDeclaredLength_LeavesItToTheStream()
    {
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new MemoryStream(Payload);

        await CreateSut().UploadAudioFileAsync(UploadUrl, stream, contentLength: 0, "audio/mp4");

        _handler.Requests.Single().ContentLength.Should().Be(Payload.Length);
    }

    [Fact]
    public async Task UploadAudioFileAsync_RewindsASeekableStreamBeforeTheFirstAttempt()
    {
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new MemoryStream(Payload) { Position = 4 };

        await CreateSut().UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        _handler.Requests.Single().Body.Should().Equal(Payload);
    }

    [Fact]
    public async Task UploadAudioFileAsync_SendsANonSeekableStreamFromWhereItIs()
    {
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new NonSeekableStream(Payload);
        stream.ReadExactly(new byte[4]);

        await CreateSut().UploadAudioFileAsync(UploadUrl, stream, contentLength: 4, "audio/mp4");

        _handler.Requests.Single().Body.Should().Equal(5, 6, 7, 8);
    }

    [Fact]
    public async Task UploadAudioFileAsync_DoesNotRetryANonSeekableStream()
    {
        _handler.EnqueueThrow(TransportFailure(new IOException("broken pipe")));
        using var stream = new NonSeekableStream(Payload);

        var act = () => CreateSut().UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        await act.Should().ThrowAsync<HttpRequestException>();
        _handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task UploadAudioFileAsync_BacksOffBeforeEachRetryWithTheAttemptNumber()
    {
        _handler.EnqueueThrow(TransportFailure(new IOException("reset")));
        _handler.EnqueueThrow(TransportFailure(new IOException("reset")));
        _handler.EnqueueThrow(TransportFailure(new IOException("reset")));
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new MemoryStream(Payload);
        var sut = CreateSut();

        await sut.UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        sut.UploadBackoffAttempts.Should().Equal(1, 2, 3);
        _handler.Requests.Should().HaveCount(4);
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.Shutdown)]
    [InlineData(SocketError.ConnectionAborted)]
    public async Task UploadAudioFileAsync_RetriesASocketResetOfEitherKind(SocketError error)
    {
        _handler.EnqueueThrow(TransportFailure(new SocketException((int)error)));
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new MemoryStream(Payload);

        await CreateSut().UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task UploadAudioFileAsync_RetriesAnIoFailureWithNoSocketBehindIt()
    {
        _handler.EnqueueThrow(TransportFailure(new IOException("stream closed")));
        _handler.Enqueue(HttpStatusCode.OK);
        using var stream = new MemoryStream(Payload);

        await CreateSut().UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task UploadAudioFileAsync_DoesNotRetryARefusedConnection()
    {
        _handler.EnqueueThrow(TransportFailure(new SocketException((int)SocketError.ConnectionRefused)));
        using var stream = new MemoryStream(Payload);

        var act = () => CreateSut().UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        await act.Should().ThrowAsync<HttpRequestException>();
        _handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task UploadAudioFileAsync_DoesNotRetryAFailureWithNoInnerCause()
    {
        _handler.EnqueueThrow(new HttpRequestException("no route to host"));
        using var stream = new MemoryStream(Payload);

        var act = () => CreateSut().UploadAudioFileAsync(UploadUrl, stream, Payload.Length, "audio/mp4");

        await act.Should().ThrowAsync<HttpRequestException>();
        _handler.Requests.Should().ContainSingle();
    }

    // =========================================================================
    // PollTranscodeStatusAsync
    // =========================================================================

    [Fact]
    public async Task PollTranscodeStatusAsync_ReturnsAsSoonAsAShaAppears()
    {
        _handler.Enqueue(HttpStatusCode.OK, PollDoneJson);
        var sut = CreateSut();

        var result = await sut.PollTranscodeStatusAsync(Token, "up-1");

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be($"{ApiBase}/media/upload/up-1/transcoded?loudnorm=false");
        request.Authorization.Should().Be($"Bearer {Token}");
        result.Should().Be(new YotoTranscodeResponse("sha-1", null, "complete"));
        sut.PollDelays.Should().Be(0);
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_KeepsPollingUntilTheShaAppears()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"transcode":{"status":"processing"}}""");
        _handler.Enqueue(HttpStatusCode.OK, """{"transcode":{"status":"processing"}}""");
        _handler.Enqueue(HttpStatusCode.OK, PollDoneJson);
        var sut = CreateSut();

        var result = await sut.PollTranscodeStatusAsync(Token, "up-1");

        result.TranscodedSha256.Should().Be("sha-1");
        _handler.Requests.Should().HaveCount(3);
        sut.PollDelays.Should().Be(2);
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_ReadsAFlatResponseAndFallsBackToTranscodeStatus()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"transcodedSha256":"sha-r","transcodeStatus":"queued"}""");

        var result = await CreateSut().PollTranscodeStatusAsync(Token, "up-1");

        result.Should().Be(new YotoTranscodeResponse("sha-r", null, "queued"));
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_PrefersStatusOverTranscodeStatus()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"transcodedSha256":"s","status":"a","transcodeStatus":"b"}""");

        (await CreateSut().PollTranscodeStatusAsync(Token, "up-1")).Status.Should().Be("a");
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_ANonObjectTranscodeKey_FallsBackToTheRoot()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"transcode":"x","transcodedSha256":"sha-x"}""");

        (await CreateSut().PollTranscodeStatusAsync(Token, "up-1")).TranscodedSha256.Should().Be("sha-x");
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_ReadsTranscodedInfo_SoTheCardCanDeclareWhatYotoActuallyMade()
    {
        // A card whose track Format doesn't match what Yoto really transcoded to fails on the
        // player after a couple of seconds — this is the data that lets the caller get it right.
        _handler.Enqueue(HttpStatusCode.OK, """
            {"transcode":{"transcodedSha256":"sha-1","status":"complete",
             "transcodedInfo":{"duration":1970.4,"fileSize":16898819,"channels":"stereo","format":"opus"}}}
            """);

        var result = await CreateSut().PollTranscodeStatusAsync(Token, "up-1");

        result.TranscodedInfo.Should().Be(new YotoTranscodedInfo(1970.4, 16898819, "stereo", "opus"));
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_TranscodedInfoThatIsNotAnObject_IsTreatedAsAbsent()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"transcodedSha256":"sha-1","status":"complete","transcodedInfo":"not an object"}""");

        var result = await CreateSut().PollTranscodeStatusAsync(Token, "up-1");

        result.TranscodedInfo.Should().BeNull();
    }

    public static TheoryData<string, string> TranscodedInfoMissingOrWrongTypeFields => new()
    {
        // Partial or wrong-typed info is worse than none: falling back to the caller's own default is
        // safer than reporting an object with a silently-wrong field (e.g. Format defaulting to "").
        { "duration missing", """{"fileSize":16898819,"channels":"stereo","format":"opus"}""" },
        { "duration wrong type", """{"duration":"long","fileSize":16898819,"channels":"stereo","format":"opus"}""" },
        { "fileSize missing", """{"duration":1970.4,"channels":"stereo","format":"opus"}""" },
        { "fileSize wrong type", """{"duration":1970.4,"fileSize":"big","channels":"stereo","format":"opus"}""" },
        { "channels missing", """{"duration":1970.4,"fileSize":16898819,"format":"opus"}""" },
        { "channels wrong type", """{"duration":1970.4,"fileSize":16898819,"channels":2,"format":"opus"}""" },
        { "format missing", """{"duration":1970.4,"fileSize":16898819,"channels":"stereo"}""" },
        { "format wrong type", """{"duration":1970.4,"fileSize":16898819,"channels":"stereo","format":7}""" },
    };

    [Theory]
    [MemberData(nameof(TranscodedInfoMissingOrWrongTypeFields))]
    public async Task PollTranscodeStatusAsync_TranscodedInfoMissingOrWrongTypeInOneField_IsTreatedAsAbsent(string because, string infoJson)
    {
        _handler.Enqueue(HttpStatusCode.OK, $$"""{"transcodedSha256":"sha-1","status":"complete","transcodedInfo":{{infoJson}}}""");

        var result = await CreateSut().PollTranscodeStatusAsync(Token, "up-1");

        result.TranscodedInfo.Should().BeNull(because);
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_NonStringFieldsCountAsAbsent()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"transcodedSha256":5,"status":{}}""");
        _handler.Enqueue(HttpStatusCode.OK, PollDoneJson);

        var result = await CreateSut().PollTranscodeStatusAsync(Token, "up-1");

        result.TranscodedSha256.Should().Be("sha-1");
        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_GivesUpAfter360PollsWithATimeout()
    {
        _handler.RespondAlways(HttpStatusCode.OK, """{"transcode":{"status":"processing"}}""");
        var sut = CreateSut();

        var act = () => sut.PollTranscodeStatusAsync(Token, "up-1");

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("Transcode polling timed out for upload up-1 after 360 attempts (~30 min)");
        _handler.Requests.Should().HaveCount(360);
        sut.PollDelays.Should().Be(360);
    }

    [Fact]
    public async Task PollTranscodeStatusAsync_ARefusedPoll_ThrowsWithoutRetrying()
    {
        _handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

        var act = () => CreateSut().PollTranscodeStatusAsync(Token, "up-1");

        await act.Should().ThrowAsync<HttpRequestException>();
        _handler.Requests.Should().ContainSingle();
    }

    // =========================================================================
    // UploadAndTranscodeAsync — the three steps in order, with progress
    // =========================================================================

    [Fact]
    public async Task UploadAndTranscodeAsync_RunsUrlUploadPollInOrderAndReturnsTheSha()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"upload":{"uploadUrl":"https://s3.example/put","uploadId":"up-7"}}""");
        _handler.Enqueue(HttpStatusCode.OK);
        _handler.Enqueue(HttpStatusCode.OK, PollDoneJson);
        var progress = new RecordingProgress();
        using var stream = new MemoryStream(Payload);

        var result = await CreateSut().UploadAndTranscodeAsync(Token, stream, Payload.Length, "audio/mp4", progress);

        result.Sha256.Should().Be("sha-1");
        _handler.Requests.Select(r => (r.Method.Method, r.Uri)).Should().Equal(
            ("GET", $"{ApiBase}/media/transcode/audio/uploadUrl"),
            ("PUT", "https://s3.example/put"),
            ("GET", $"{ApiBase}/media/upload/up-7/transcoded?loudnorm=false"));
        _handler.Requests[1].Body.Should().Equal(Payload);
        _handler.Requests[1].ContentType.Should().Be("audio/mp4");
        progress.Reports.Should().Equal(10, 30, 60, 100);
    }

    [Fact]
    public async Task UploadAndTranscodeAsync_WithoutAProgressSink_StillCompletes()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"upload":{"uploadUrl":"https://s3.example/put","uploadId":"up-8"}}""");
        _handler.Enqueue(HttpStatusCode.OK);
        _handler.Enqueue(HttpStatusCode.OK, PollDoneJson);
        using var stream = new MemoryStream(Payload);

        (await CreateSut().UploadAndTranscodeAsync(Token, stream, Payload.Length, "audio/mp4")).Sha256.Should().Be("sha-1");
    }

    // =========================================================================
    // Icons
    // =========================================================================

    [Fact]
    public async Task GetPublicIconsAsync_MapsTheIconList()
    {
        _handler.Enqueue(HttpStatusCode.OK, """[{"mediaId":"m1","title":"Star","publicTags":["shape","night"],"url":"https://i.example/1.png"}]""");

        var icons = await CreateSut().GetPublicIconsAsync(Token);

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be($"{ApiBase}/media/displayIcons/public");
        request.Authorization.Should().Be($"Bearer {Token}");
        icons.Should().BeEquivalentTo([new YotoPublicIcon("m1", "Star", ["shape", "night"], "https://i.example/1.png")]);
    }

    [Fact]
    public async Task GetPublicIconsAsync_ANullBody_ReturnsAnEmptyList()
    {
        _handler.Enqueue(HttpStatusCode.OK, "null");

        (await CreateSut().GetPublicIconsAsync(Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetPublicIconsAsync_ARefusedRequest_Throws()
    {
        _handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

        var act = () => CreateSut().GetPublicIconsAsync(Token);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task UploadCustomIconAsync_PostsTheRawBytesAsPngWithTheEscapedFilename()
    {
        // Confirmed live 2026-09-26: multipart returns 400 "A binary image file is required" even
        // for a real, valid image; a raw body with Content-Type: image/png succeeds, same shape as
        // the cover endpoint. Every icon upload had silently failed since this feature was written.
        // The response body is nested under "displayIcon" (confirmed live) — not the flat
        // { mediaId, url } shape this test used to assume, which is exactly what let a null
        // MediaId slip through and fail card creation with "must be 43 characters" for every icon.
        _handler.Enqueue(HttpStatusCode.OK,
            """{"displayIcon":{"mediaId":"icon-1","userId":"u1","displayIconId":"d1","url":"https://i.example/icon-1.png"}}""");
        byte[] icon = [0x89, 0x50, 0x4E, 0x47];

        var result = await CreateSut().UploadCustomIconAsync(Token, icon, "my icon.png");

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be($"{ApiBase}/media/displayIcons/user/me/upload?autoConvert=true&filename=my%20icon.png");
        request.Authorization.Should().Be($"Bearer {Token}");
        request.ContentType.Should().Be("image/png");
        request.Body.Should().Equal(icon);
        result.Should().Be(new YotoIconUploadResponse("icon-1", "https://i.example/icon-1.png"));
    }

    [Fact]
    public async Task UploadCustomIconAsync_UrlComesBackAsAnEmptyObject_StillReturnsTheMediaId()
    {
        // Confirmed live 2026-09-26: Yoto sometimes returns "url": {} (an empty object, not a
        // string) — presumably while autoConvert is still processing. Reading that with
        // JsonElement.GetString() throws InvalidOperationException instead of returning null,
        // which silently failed EVERY icon attach even though the upload itself, and the mediaId
        // card creation actually needs, had both succeeded.
        _handler.Enqueue(HttpStatusCode.OK,
            """{"displayIcon":{"mediaId":"icon-1","userId":"u1","displayIconId":"d1","url":{}}}""");

        var result = await CreateSut().UploadCustomIconAsync(Token, [1], "a.png");

        result.Should().Be(new YotoIconUploadResponse("icon-1", null));
    }

    [Fact]
    public async Task UploadCustomIconAsync_AnEmptyBody_Throws()
    {
        _handler.Enqueue(HttpStatusCode.OK, "null");

        var act = () => CreateSut().UploadCustomIconAsync(Token, [1], "a.png");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Failed to upload custom icon");
    }

    [Fact]
    public async Task UploadCustomIconAsync_ARefusedUpload_Throws()
    {
        _handler.Enqueue(HttpStatusCode.BadRequest, "{}");

        var act = () => CreateSut().UploadCustomIconAsync(Token, [1], "a.png");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // Cover image
    // =========================================================================

    [Fact]
    public async Task UploadCoverImageAsync_PostsTheRawBytesAsJpegAndReturnsTheUrl()
    {
        _handler.Enqueue(HttpStatusCode.OK, """{"coverImage":{"mediaUrl":"https://c.example/cover.jpg"}}""");
        using var image = new MemoryStream(Payload);

        var url = await CreateSut().UploadCoverImageAsync(Token, image);

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be($"{ApiBase}/media/coverImage/user/me/upload?autoConvert=true&coverType=default");
        request.Authorization.Should().Be($"Bearer {Token}");
        request.ContentType.Should().Be("image/jpeg");
        request.Body.Should().Equal(Payload);
        url.Should().Be("https://c.example/cover.jpg");
    }

    [Theory]
    [InlineData("""{"mediaUrl":"a"}""", "a")]
    [InlineData("""{"imageL":"b"}""", "b")]
    [InlineData("""{"url":"c"}""", "c")]
    [InlineData("""{"coverImageL":"d"}""", "d")]
    [InlineData("""{"coverImage":{"url":"e"}}""", "e")]
    [InlineData("""{"cover":{"url":"f"}}""", "f")]
    [InlineData("""{"upload":{"url":"g"}}""", "g")]
    [InlineData("""{"media":{"url":"h"}}""", "h")]
    public async Task UploadCoverImageAsync_FindsTheUrlUnderEachKnownKeyAndWrapper(string json, string expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, json);
        using var image = new MemoryStream(Payload);

        (await CreateSut().UploadCoverImageAsync(Token, image)).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"coverImageL":"4","url":"3","imageL":"2","mediaUrl":"1"}""", "1")]
    [InlineData("""{"coverImageL":"4","url":"3","imageL":"2"}""", "2")]
    [InlineData("""{"coverImageL":"4","url":"3"}""", "3")]
    [InlineData("""{"url":"root","coverImage":{"mediaUrl":"nested"}}""", "root")]
    [InlineData("""{"media":{"url":"m"},"upload":{"url":"u"},"cover":{"url":"c"},"coverImage":{"url":"ci"}}""", "ci")]
    [InlineData("""{"media":{"url":"m"},"upload":{"url":"u"},"cover":{"url":"c"}}""", "c")]
    [InlineData("""{"media":{"url":"m"},"upload":{"url":"u"}}""", "u")]
    [InlineData("""{"mediaUrl":5,"url":"text"}""", "text")]
    [InlineData("""{"coverImage":"not-an-object","url":"root"}""", "root")]
    public async Task UploadCoverImageAsync_PrefersKeysAndWrappersInAFixedOrder(string json, string expected)
    {
        _handler.Enqueue(HttpStatusCode.OK, json);
        using var image = new MemoryStream(Payload);

        (await CreateSut().UploadCoverImageAsync(Token, image)).Should().Be(expected);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("""{"coverImage":{"other":"x"}}""")]
    public async Task UploadCoverImageAsync_NoCoverUrlInTheResponse_Throws(string json)
    {
        _handler.Enqueue(HttpStatusCode.OK, json);
        using var image = new MemoryStream(Payload);

        var act = () => CreateSut().UploadCoverImageAsync(Token, image);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No cover URL in Yoto response");
    }

    [Fact]
    public async Task UploadCoverImageAsync_ARefusedUpload_Throws()
    {
        _handler.Enqueue(HttpStatusCode.RequestEntityTooLarge, """{"url":"ignored"}""");
        using var image = new MemoryStream(Payload);

        var act = () => CreateSut().UploadCoverImageAsync(Token, image);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // Test support
    // =========================================================================

    private const string TokenJson = """{"access_token":"a","refresh_token":"r","token_type":"Bearer","expires_in":1}""";

    private static readonly YotoCardContent SampleContent = new(
        [new YotoChapter("01", "Chapter", [new YotoTrack("01", "Track", "yoto:#sha", "aac", "audio", 12.5, 1000, "stereo", null)], null)],
        null, "linear", "1");

    private static readonly YotoCardMetadata SampleMetadata = new("Ann", null, "A card", null, null, 3, 7, null, null);

    private static HttpRequestException TransportFailure(Exception inner) =>
        new("Error while copying content to a stream.", inner);

    private TestableYotoService CreateSut(
        string? clientId = "client-1", string? clientSecret = null, string? apiBase = null, string? authBase = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Yoto:ClientId"] = clientId,
            ["Yoto:ClientSecret"] = clientSecret,
            ["Yoto:ApiBase"] = apiBase,
            ["Yoto:AuthBase"] = authBase,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns((string name) =>
            {
                _clientNames.Add(name);
                return new HttpClient(_handler, disposeHandler: false);
            });
        return new TestableYotoService(factory.Object, configuration);
    }

    private sealed class TestableYotoService(IHttpClientFactory factory, IConfiguration configuration)
        : YotoService(factory, configuration, Mock.Of<ILogger<YotoService>>())
    {
        public List<int> UploadBackoffAttempts { get; } = [];
        public int PollDelays { get; private set; }

        protected override Task DelayBetweenUploadAttemptsAsync(int attempt, CancellationToken ct)
        {
            UploadBackoffAttempts.Add(attempt);
            return Task.CompletedTask;
        }

        protected override Task DelayBetweenTranscodePollsAsync(CancellationToken ct)
        {
            PollDelays++;
            return Task.CompletedTask;
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Reports { get; } = [];

        public void Report(int value) => Reports.Add(value);
    }

    private sealed record RecordedRequest(
        HttpMethod Method, string Uri, string? Authorization, string? ContentType, long? ContentLength, byte[] Body)
    {
        public string BodyText => Encoding.UTF8.GetString(Body);

        public Dictionary<string, string> Form
        {
            get
            {
                var query = HttpUtility.ParseQueryString(BodyText);
                return query.AllKeys.ToDictionary(key => key!, key => query[key]!);
            }
        }
    }

    /// <summary>Records every request (reading the body eagerly) and replies from a queue, then a fallback.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        private Func<HttpResponseMessage>? _fallback;

        public List<RecordedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body = "") =>
            _responses.Enqueue(() => Response(status, body));

        public void EnqueueThrow(Exception exception) =>
            _responses.Enqueue(() => throw exception);

        public void RespondAlways(HttpStatusCode status, string body) =>
            _fallback = () => Response(status, body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content;
            var contentLength = content?.Headers.ContentLength;
            var body = content is null ? [] : await content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                content?.Headers.ContentType?.ToString(),
                contentLength,
                body));

            var respond = _responses.Count > 0 ? _responses.Dequeue() : _fallback
                ?? throw new InvalidOperationException($"Unexpected request #{Requests.Count}: {request.Method} {request.RequestUri}");
            return respond();
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
