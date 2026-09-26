using System.Net;
using System.Text;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Infrastructure.Services.Yoto;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class YotoServiceIconUploadTests
{
    [Fact]
    public async Task UploadCustomIconAsync_SendsTheRawBytesAsPngWithTheEscapedFilename()
    {
        // Confirmed live against the real Yoto API: a multipart "file" part gets a 400
        // "A binary image file is required" even for a real, valid image, so this pins the
        // request shape that actually works — a raw body with Content-Type: image/png, the
        // same shape UploadCoverImageAsync already uses for the cover endpoint.
        // The response body is nested under "displayIcon" (confirmed live) — not a flat
        // { mediaId, url } shape, which is what let a null MediaId slip through undetected.
        var handler = new CapturingHandler(
            """{"displayIcon":{"mediaId":"icon-1","userId":"u1","displayIconId":"d1","url":"https://i.example/icon-1.png"}}""");
        var sut = CreateSut(handler);
        byte[] icon = [0x89, 0x50, 0x4E, 0x47];

        var result = await sut.UploadCustomIconAsync("token-1", icon, "my icon.png");

        handler.LastRequestUri.Should().Contain("/media/displayIcons/user/me/upload")
            .And.Contain("filename=my%20icon.png");
        handler.LastContentType.Should().Be("image/png");
        handler.LastBody.Should().Equal(icon);
        result.Should().Be(new YotoIconUploadResponse("icon-1", "https://i.example/icon-1.png"));
    }

    [Fact]
    public async Task UploadCustomIconAsync_UrlComesBackAsAnEmptyObject_StillReturnsTheMediaId()
    {
        // Confirmed live: Yoto sometimes returns "url": {} (an empty object, not a string) —
        // presumably while autoConvert is still processing. Reading that with
        // JsonElement.GetString() throws InvalidOperationException instead of returning null,
        // which silently failed EVERY icon attach even though the upload itself, and the mediaId
        // card creation actually needs, had both succeeded.
        var handler = new CapturingHandler(
            """{"displayIcon":{"mediaId":"icon-1","userId":"u1","displayIconId":"d1","url":{}}}""");
        var sut = CreateSut(handler);

        var result = await sut.UploadCustomIconAsync("token-1", [1, 2, 3], "a.png");

        result.Should().Be(new YotoIconUploadResponse("icon-1", null));
    }

    [Fact]
    public async Task UploadCustomIconAsync_ARefusedUpload_Throws()
    {
        var handler = new CapturingHandler("""{"error":"bad-request"}""", HttpStatusCode.BadRequest);
        var sut = CreateSut(handler);

        var act = () => sut.UploadCustomIconAsync("token-1", [1, 2, 3], "a.png");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static YotoService CreateSut(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Yoto")).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new YotoService(factory.Object, new ConfigurationBuilder().Build(), Mock.Of<ILogger<YotoService>>());
    }

    private sealed class CapturingHandler(string jsonResponse, HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }
        public string? LastContentType { get; private set; }
        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.PathAndQuery;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        }
    }
}
