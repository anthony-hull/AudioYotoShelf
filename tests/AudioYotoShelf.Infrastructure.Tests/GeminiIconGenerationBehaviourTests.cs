using System.Net;
using System.Text;
using System.Text.Json;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Infrastructure.Services.IconGeneration;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AudioYotoShelf.Infrastructure.Tests;

/// <summary>
/// The Gemini HTTP contract, the image post-processing and the public-icon search.
/// Privacy property: with no Gemini:ApiKey the app must never send a book's metadata to Google,
/// so nothing may reach the HTTP layer at all.
/// </summary>
public class GeminiIconGenerationBehaviourTests
{
    private const string PublicIconsCacheKey = "yoto:public_icons";

    private sealed class RecordingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return respond();
        }
    }

    private sealed record Harness(
        GeminiIconGenerationService Sut, RecordingHandler Handler, Mock<IHttpClientFactory> Factory, IDistributedCache Cache);

    private static Harness Create(Func<HttpResponseMessage>? respond = null, Dictionary<string, string?>? settings = null)
    {
        var handler = new RecordingHandler(respond ?? (() => ImageResponse(Png(32, 32, (_, _) => new Rgba32(200, 50, 50, 255)))));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Gemini")).Returns(() => new HttpClient(handler));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?> { ["Gemini:ApiKey"] = "test-key" })
            .Build();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        var sut = new GeminiIconGenerationService(
            factory.Object, cache, config, Mock.Of<ILogger<GeminiIconGenerationService>>());
        return new Harness(sut, handler, factory, cache);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ImageResponse(byte[] png) => Json(
        """{"candidates":[{"content":{"parts":[{"text":"here you go"},{"inlineData":{"mimeType":"image/png","data":""" +
        $"\"{Convert.ToBase64String(png)}\"" + "}}]}}]}");

    private static byte[] Png(int width, int height, Func<int, int, Rgba32> pixelAt)
    {
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) row[x] = pixelAt(x, y);
            }
        });
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static Image<Rgba32> Decode(byte[] png) => Image.Load<Rgba32>(png);

    // =========================================================================
    // Privacy: no API key => nothing leaves the process
    // =========================================================================

    [Fact]
    public async Task GenerateIcon_WithoutApiKey_ThrowsAndSendsNoRequest()
    {
        var h = Create(settings: new Dictionary<string, string?>());

        var act = () => h.Sut.GenerateIconAsync("a book about dragons");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Gemini:ApiKey*");
        h.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateChapterIcon_WithoutApiKey_SendsNoRequest()
    {
        var h = Create(settings: new Dictionary<string, string?>());

        var act = () => h.Sut.GenerateChapterIconAsync("Chapter 1", "Secret Diary", "Mystery");

        await act.Should().ThrowAsync<InvalidOperationException>();
        h.Handler.Requests.Should().BeEmpty();
    }

    // =========================================================================
    // Request contract
    // =========================================================================

    [Fact]
    public async Task GenerateIcon_PostsThePromptToTheConfiguredModelWithTheKey()
    {
        var h = Create(settings: new Dictionary<string, string?>
        {
            ["Gemini:ApiKey"] = "k1",
            ["Gemini:Model"] = "my-model",
        });

        await h.Sut.GenerateIconAsync("a cat");

        var request = h.Handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be("https://generativelanguage.googleapis.com/v1beta/models/my-model:generateContent?key=k1");
        h.Factory.Verify(f => f.CreateClient("Gemini"), Times.Once);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("a cat");
        var config = root.GetProperty("generationConfig");
        config.GetProperty("responseMimeType").GetString().Should().Be("text/plain");
        config.GetProperty("responseModalities").EnumerateArray().Select(m => m.GetString())
            .Should().Equal("TEXT", "IMAGE");
    }

    [Fact]
    public async Task GenerateIcon_WithoutConfiguredModel_UsesTheDefaultModel()
    {
        var h = Create();

        await h.Sut.GenerateIconAsync("a cat");

        h.Handler.Requests.Single().Uri.Should().Contain("/models/gemini-3.1-flash-image:generateContent?key=test-key");
    }

    [Fact]
    public async Task GenerateChapterIcon_SendsTheChapterIconPromptForThatChapter()
    {
        var h = Create();

        await h.Sut.GenerateChapterIconAsync("The Cave", "Dragon Book", "Fantasy");

        using var body = JsonDocument.Parse(h.Handler.Requests.Single().Body);
        body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()
            .Should().Be(h.Sut.BuildChapterIconPrompt("The Cave", "Dragon Book", "Fantasy"));
    }

    // =========================================================================
    // Response handling
    // =========================================================================

    [Fact]
    public async Task GenerateIcon_ReturnsTheImagePartResizedTo16x16()
    {
        // The response lists a text part first; the image part must still be found.
        var h = Create();

        var icon = await h.Sut.GenerateIconAsync("a cat");

        using var image = Decode(icon);
        (image.Width, image.Height).Should().Be((16, 16));
        image[0, 0].Should().Be(new Rgba32(200, 50, 50, 255));
    }

    [Fact]
    public async Task GenerateIcon_ApiError_ThrowsWithTheStatusCode()
    {
        var h = Create(() => Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError));

        var act = () => h.Sut.GenerateIconAsync("a cat");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Gemini API error: InternalServerError");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{}]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"sorry, no image"}]}}]}""")]
    public async Task GenerateIcon_ResponseWithoutAnImage_ThrowsANoImageError(string responseJson)
    {
        var h = Create(() => Json(responseJson));

        var act = () => h.Sut.GenerateIconAsync("a cat");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Gemini did not return an image");
    }

    // =========================================================================
    // Prompt wording that carries requirements
    // =========================================================================

    [Fact]
    public void BuildChapterIconPrompt_WithoutGenre_HasNoGenreSentence()
    {
        var prompt = new GeminiIconGenerationService(
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IDistributedCache>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<GeminiIconGenerationService>>()).BuildChapterIconPrompt("Ch", "Test", null);

        prompt.Should().Contain("from the book \"Test\". Use simple shapes")
            .And.Contain("limited color palette");
    }

    [Fact]
    public void BuildChapterIconPrompt_WithGenre_PutsTheGenreSentenceBeforeTheStyleRules()
    {
        var h = Create();

        h.Sut.BuildChapterIconPrompt("Ch", "Test", "Fantasy")
            .Should().Contain("from the book \"Test\". The genre is Fantasy. Use simple shapes");
    }

    // =========================================================================
    // Resizing and the "no pure black" rule for the LED display
    // =========================================================================

    [Fact]
    public async Task ConvertCoverToIcon_StretchesANonSquareCoverTo16x16()
    {
        var h = Create();
        using var cover = new MemoryStream(Png(32, 8, (_, _) => new Rgba32(10, 20, 30, 255)));

        var icon = await h.Sut.ConvertCoverToIconAsync(cover);

        using var image = Decode(icon);
        (image.Width, image.Height).Should().Be((16, 16));
        image[15, 15].Should().Be(new Rgba32(10, 20, 30, 255));
    }

    [Fact]
    public async Task ConvertCoverToIcon_LightensOnlyOpaqueEnoughPureBlackPixels()
    {
        // Column index picks the colour; every row is the same, so a skipped row is caught too.
        var columns = new Dictionary<int, (Rgba32 Source, Rgba32 Expected)>
        {
            [0] = (new Rgba32(0, 0, 0, 255), new Rgba32(8, 8, 8, 255)),       // pure black -> very dark grey
            [1] = (new Rgba32(0, 0, 0, 0), new Rgba32(0, 0, 0, 0)),           // fully transparent: left alone
            [2] = (new Rgba32(0, 0, 0, 128), new Rgba32(8, 8, 8, 128)),       // translucent black keeps its alpha
            [3] = (new Rgba32(1, 0, 0, 255), new Rgba32(1, 0, 0, 255)),
            [4] = (new Rgba32(0, 1, 0, 255), new Rgba32(0, 1, 0, 255)),
            [5] = (new Rgba32(0, 0, 1, 255), new Rgba32(0, 0, 1, 255)),
            [6] = (new Rgba32(0, 200, 200, 255), new Rgba32(0, 200, 200, 255)),
            [7] = (new Rgba32(200, 0, 200, 255), new Rgba32(200, 0, 200, 255)),
            [8] = (new Rgba32(200, 200, 0, 255), new Rgba32(200, 200, 0, 255)),
        };
        var fallback = new Rgba32(200, 50, 50, 255);
        var h = Create();
        using var cover = new MemoryStream(Png(16, 16, (x, _) => columns.TryGetValue(x, out var c) ? c.Source : fallback));

        var icon = await h.Sut.ConvertCoverToIconAsync(cover);

        using var image = Decode(icon);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var expected = columns.TryGetValue(x, out var c) ? c.Expected : fallback;
                image[x, y].Should().Be(expected, $"pixel ({x},{y})");
            }
        }
    }

    // =========================================================================
    // Public icon search over the cached Yoto library
    // =========================================================================

    private static readonly YotoPublicIcon[] LibraryIcons =
    [
        new("m1", "Dragon", ["fire", "fantasy"], "https://icons/dragon.png"),
        new("m2", "Castle", ["dragon-lair", "stone"], "https://icons/castle.png"),
        new("m3", "Cat", ["pet"], "https://icons/cat.png"),
        new("m4", "Fish", ["water", "swim"], "https://icons/fish.png"),
    ];

    private static async Task<Harness> CreateWithLibraryAsync()
    {
        var h = Create();
        await h.Cache.SetStringAsync(PublicIconsCacheKey, JsonSerializer.Serialize(LibraryIcons));
        return h;
    }

    [Fact]
    public async Task SearchPublicIcons_MatchesTitlesAndTagsIgnoringCase_InLibraryOrder()
    {
        var h = await CreateWithLibraryAsync();

        var result = await h.Sut.SearchPublicIconsAsync("DRAGON");

        result.Select(i => i.Title).Should().Equal("Dragon", "Castle");
    }

    [Fact]
    public async Task SearchPublicIcons_ReturnsIconsMatchingAnyTermInTheQuery()
    {
        var h = await CreateWithLibraryAsync();

        var result = await h.Sut.SearchPublicIconsAsync("cat water");

        result.Select(i => i.Title).Should().Equal("Cat", "Fish");
    }

    [Fact]
    public async Task SearchPublicIcons_MatchesWhenOnlyOneOfSeveralTagsMatches()
    {
        var h = await CreateWithLibraryAsync();

        var result = await h.Sut.SearchPublicIconsAsync("stone");

        result.Select(i => i.Title).Should().Equal("Castle");
    }

    [Fact]
    public async Task SearchPublicIcons_ReturnsTheFirstMatchesUpToMaxResults()
    {
        var h = await CreateWithLibraryAsync();

        var result = await h.Sut.SearchPublicIconsAsync("dragon", maxResults: 1);

        result.Select(i => i.Title).Should().Equal("Dragon");
    }

    [Theory]
    [InlineData("zebra")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchPublicIcons_NoMatchOrBlankQuery_ReturnsNothing(string query)
    {
        var h = await CreateWithLibraryAsync();

        (await h.Sut.SearchPublicIconsAsync(query)).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchPublicIcons_EmptyCachedLibrary_ReturnsNothing()
    {
        var h = Create();
        await h.Cache.SetStringAsync(PublicIconsCacheKey, "[]");

        (await h.Sut.SearchPublicIconsAsync("dragon")).Should().BeEmpty();
    }
}
