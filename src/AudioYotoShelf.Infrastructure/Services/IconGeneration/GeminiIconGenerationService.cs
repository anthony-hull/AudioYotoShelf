using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AudioYotoShelf.Infrastructure.Services.IconGeneration;

public class GeminiIconGenerationService(
    IHttpClientFactory httpClientFactory,
    IDistributedCache cache,
    IConfiguration configuration,
    ILogger<GeminiIconGenerationService> logger) : IIconGenerationService
{
    private const string PublicIconsCacheKey = "yoto:public_icons";
    private static readonly TimeSpan PublicIconsCacheTtl = TimeSpan.FromHours(24);

    private string ApiKey => configuration["Gemini:ApiKey"]
        ?? throw new InvalidOperationException("Gemini:ApiKey not configured");
    private string Model => configuration.GetValue("Gemini:Model", "gemini-3.1-flash-image")!;

    // Confirmed live 2026-09-26: "512" is a real, honored value (the 400 for an invalid one lists
    // the full accepted set: 1K, 2K, 4K, 512, 512P, 512PX) and genuinely halves the returned image's
    // dimensions (704x384 vs 1408x768 for the unset default). It's also the cheapest Gemini pricing
    // tier ($0.045/image vs $0.067 for the 1K default). The icon pipeline immediately downsamples
    // to 16x16 anyway, so the extra native resolution was paying for detail that only made the
    // final shrink noisier, never better — smaller source, cheaper AND cleaner output.
    private string ImageSize => configuration.GetValue("Gemini:ImageSize", "512")!;

    public virtual async Task<byte[]> GenerateIconAsync(string prompt, CancellationToken ct = default)
    {
        logger.LogInformation("Generating icon via Gemini: {Prompt}", prompt);

        using var client = httpClientFactory.CreateClient("Gemini");
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={ApiKey}";

        var request = new GeminiRequest
        {
            Contents =
            [
                new GeminiContent
                {
                    Parts = [new GeminiPart { Text = prompt }]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig
            {
                ResponseMimeType = "text/plain",
                ResponseModalities = ["TEXT", "IMAGE"],
                ImageConfig = new GeminiImageConfig { ImageSize = ImageSize }
            }
        };

        var response = await client.PostAsJsonAsync(url, request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Gemini API error: {StatusCode} {Body}", response.StatusCode, errorBody);
            throw new InvalidOperationException($"Gemini API error: {response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(ct);

        // Extract image from response
        var imagePart = result?.Candidates?.FirstOrDefault()?
            .Content?.Parts?.FirstOrDefault(p => p.InlineData is not null);

        if (imagePart?.InlineData is null)
        {
            logger.LogWarning("Gemini returned no image for prompt: {Prompt}", prompt);
            throw new InvalidOperationException("Gemini did not return an image");
        }

        var rawImageBytes = Convert.FromBase64String(imagePart.InlineData.Data!);

        // Resize to 16x16 using nearest-neighbor interpolation
        return ResizeTo16X16(rawImageBytes);
    }

    public virtual async Task<byte[]> GenerateChapterIconAsync(
        string chapterTitle, string bookTitle, string? genre, CancellationToken ct = default)
    {
        var prompt = BuildChapterIconPrompt(chapterTitle, bookTitle, genre);
        return await GenerateIconAsync(prompt, ct);
    }

    public virtual async Task<byte[]> ConvertCoverToIconAsync(Stream coverImage, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await coverImage.CopyToAsync(ms, ct);
        return ResizeTo16X16(ms.ToArray());
    }

    public virtual async Task<YotoPublicIcon[]> SearchPublicIconsAsync(
        string query, int maxResults = 10, CancellationToken ct = default)
    {
        // Try cache first
        var cached = await cache.GetStringAsync(PublicIconsCacheKey, ct);
        YotoPublicIcon[]? allIcons;

        if (cached is not null)
        {
            allIcons = JsonSerializer.Deserialize<YotoPublicIcon[]>(cached);
        }
        else
        {
            allIcons = null;
            logger.LogInformation("Public icons not in cache; caller should populate via IYotoService");
        }

        if (allIcons is null || allIcons.Length == 0)
            return [];

        // Simple search: match title or tags
        var queryLower = query.ToLowerInvariant();
        var queryTerms = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return allIcons
            .Where(icon =>
                queryTerms.Any(term =>
                    icon.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    icon.PublicTags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .Take(maxResults)
            .ToArray();
    }

    public virtual string BuildChapterIconPrompt(string chapterTitle, string bookTitle, string? genre)
    {
        // Confirmed live 2026-09-26: a detailed/shaded source image looks fine at Gemini's native
        // resolution but turns to noise once nearest-neighbor-resized to the real 16x16 — a
        // transparent background gets anti-aliased edge pixels that land at random alpha values
        // when sampled down, and fine shading/gradients don't survive either. Comparing against
        // Yoto's own built-in icons (flat, single-subject, high-contrast) and re-testing the actual
        // 16x16 output (not the zoomed-in editor preview) is what caught this — the old wording
        // ("8-bit retro game sprite", "6-8 bright colors") still let Gemini render something too
        // fine-grained to survive the shrink.
        var genreHint = genre is not null ? $" The genre is {genre}." : "";
        return $"Create a 16x16 pixel art icon representing \"{chapterTitle}\" " +
               $"from the book \"{bookTitle}\".{genreHint} " +
               "Use simple shapes, limited color palette (4-5 solid, high-contrast colors, no gradients, " +
               "no shading, no anti-aliasing). Style: flat, bold, single-subject icon like a tiny app icon " +
               "or emoji — one clear silhouette centered on a plain, opaque, single-color background (not " +
               "transparent). No fine detail, no small elements, no background scenery. Every shape must " +
               "be large and blocky enough to still read clearly after being shrunk to a 16x16 pixel grid. " +
               "Thick outlines. No text. No black background. " +
               "Avoid using pure black (#000000) pixels as they appear as 'off' on LED displays.";
    }

    /// <summary>
    /// Resize image to 16x16 using nearest-neighbor interpolation to preserve pixel art crispness.
    /// Ensures no pure black pixels (replace with very dark gray).
    /// </summary>
    private static byte[] ResizeTo16X16(byte[] imageBytes)
    {
        using var image = Image.Load<Rgba32>(imageBytes);

        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(16, 16),
            Sampler = KnownResamplers.NearestNeighbor,
            Mode = ResizeMode.Stretch
        }));

        // Replace pure black pixels with very dark gray (Yoto LED consideration)
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    if (pixel.R == 0 && pixel.G == 0 && pixel.B == 0 && pixel.A > 0)
                    {
                        pixel = new Rgba32(8, 8, 8, pixel.A);
                    }
                }
            }
        });

        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }
}

// --- Gemini API DTOs (internal) ---

file record GeminiRequest
{
    [JsonPropertyName("contents")]
    public GeminiContent[] Contents { get; init; } = [];

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig? GenerationConfig { get; init; }
}

file record GeminiContent
{
    [JsonPropertyName("parts")]
    public GeminiPart[] Parts { get; init; } = [];
}

file record GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("inlineData")]
    public GeminiInlineData? InlineData { get; init; }
}

file record GeminiInlineData
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }
}

file record GeminiGenerationConfig
{
    [JsonPropertyName("responseMimeType")]
    public string? ResponseMimeType { get; init; }

    [JsonPropertyName("responseModalities")]
    public string[]? ResponseModalities { get; init; }

    [JsonPropertyName("imageConfig")]
    public GeminiImageConfig? ImageConfig { get; init; }
}

file record GeminiImageConfig
{
    [JsonPropertyName("imageSize")]
    public string? ImageSize { get; init; }
}

file record GeminiResponse
{
    [JsonPropertyName("candidates")]
    public GeminiCandidate[]? Candidates { get; init; }
}

file record GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; init; }
}
