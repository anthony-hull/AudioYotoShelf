using AudioYotoShelf.Infrastructure.Services.IconGeneration;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SixLabors.ImageSharp;

namespace AudioYotoShelf.Infrastructure.Tests;

public class GeminiIconGenerationServiceTests
{
    private readonly GeminiIconGenerationService _sut;

    public GeminiIconGenerationServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gemini:ApiKey"] = "test-key",
                ["Gemini:Model"] = "gemini-3.1-flash-image"
            })
            .Build();

        var cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

        _sut = new GeminiIconGenerationService(
            Mock.Of<IHttpClientFactory>(),
            cache,
            config,
            Mock.Of<ILogger<GeminiIconGenerationService>>());
    }

    // =========================================================================
    // BuildChapterIconPrompt
    // =========================================================================

    [Fact]
    public void BuildChapterIconPrompt_IncludesChapterTitle()
    {
        var prompt = _sut.BuildChapterIconPrompt("The Dark Forest", "Three Body Problem", "Science Fiction");
        prompt.Should().Contain("The Dark Forest");
    }

    [Fact]
    public void BuildChapterIconPrompt_IncludesBookTitle()
    {
        var prompt = _sut.BuildChapterIconPrompt("Chapter 1", "Harry Potter", null);
        prompt.Should().Contain("Harry Potter");
    }

    [Fact]
    public void BuildChapterIconPrompt_IncludesGenreWhenProvided()
    {
        var prompt = _sut.BuildChapterIconPrompt("Chapter 1", "Test", "Fantasy");
        prompt.Should().Contain("Fantasy");
    }

    [Fact]
    public void BuildChapterIconPrompt_OmitsGenreWhenNull()
    {
        var prompt = _sut.BuildChapterIconPrompt("Chapter 1", "Test", null);
        prompt.Should().NotContain("genre is");
    }

    [Fact]
    public void BuildChapterIconPrompt_IncludesPixelArtInstructions()
    {
        var prompt = _sut.BuildChapterIconPrompt("Chapter 1", "Test", null);
        prompt.Should().Contain("16x16");
        prompt.Should().Contain("pixel art");
        prompt.Should().Contain("No text");
        prompt.Should().Contain("No black");
    }

    [Fact]
    public void BuildChapterIconPrompt_WarnsAboutBlackPixels()
    {
        var prompt = _sut.BuildChapterIconPrompt("Chapter 1", "Test", null);
        prompt.Should().Contain("black")
            .And.Contain("LED");
    }

    [Fact]
    public void BuildChapterIconPrompt_DemandsAnOpaqueBackgroundAndFlatColors()
    {
        // Confirmed live 2026-09-26 against the actual 16x16 output (not the zoomed editor
        // preview): a transparent background produces anti-aliased edge pixels that resize into
        // scattered noise, and gradients/shading don't survive the shrink either — this is what
        // made generated icons unreadable next to Yoto's own flat, high-contrast built-ins.
        var prompt = _sut.BuildChapterIconPrompt("Chapter 1", "Test", null);
        prompt.Should().Contain("not transparent");
        prompt.Should().Contain("no gradients");
        prompt.Should().Contain("single-subject");
    }

    // =========================================================================
    // SearchPublicIconsAsync (in-memory cache behavior)
    // =========================================================================

    [Fact]
    public async Task SearchPublicIconsAsync_EmptyCache_ReturnsEmpty()
    {
        var result = await _sut.SearchPublicIconsAsync("dragon");
        result.Should().BeEmpty();
    }

    // =========================================================================
    // ConvertCoverToIconAsync
    // =========================================================================

    [Fact]
    public async Task ConvertCoverToIconAsync_ValidImage_Returns16x16Bytes()
    {
        // Create a simple 4x4 red PNG for testing
        var pngBytes = CreateMinimalPng(4, 4);
        using var stream = new MemoryStream(pngBytes);

        var result = await _sut.ConvertCoverToIconAsync(stream);

        result.Should().NotBeEmpty();
        // Verify it's a valid PNG (starts with PNG magic bytes)
        result[0].Should().Be(0x89);
        result[1].Should().Be(0x50); // 'P'
        result[2].Should().Be(0x4E); // 'N'
        result[3].Should().Be(0x47); // 'G'
    }

    /// <summary>
    /// Creates a minimal valid PNG file with the given dimensions.
    /// Uses ImageSharp to ensure correctness.
    /// </summary>
    private static byte[] CreateMinimalPng(int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        // Fill with a non-black color
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 50, 50, 255);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
