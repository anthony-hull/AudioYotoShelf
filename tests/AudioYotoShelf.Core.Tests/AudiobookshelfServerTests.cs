using AudioYotoShelf.Core.Configuration;
using FluentAssertions;

namespace AudioYotoShelf.Core.Tests;

public class AudiobookshelfServerTests
{
    // =========================================================================
    // ResolveConfiguredUrl — read once at startup, so a bad value fails fast
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveConfiguredUrl_NotConfigured_ReturnsNull(string? configured)
    {
        AudiobookshelfServer.ResolveConfiguredUrl(configured).Should().BeNull();
    }

    [Theory]
    [InlineData("http://abs.home", "http://abs.home")]
    [InlineData("https://abs.example.com/", "https://abs.example.com")]
    [InlineData("  https://abs.example.com:8080/  ", "https://abs.example.com:8080")]
    public void ResolveConfiguredUrl_ValidUrl_IsTrimmed(string configured, string expected)
    {
        AudiobookshelfServer.ResolveConfiguredUrl(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData("abs.example.com")]  // no scheme — starts fine, then fails on every connect
    [InlineData("ftp://abs.example.com")]
    [InlineData("not a url")]
    public void ResolveConfiguredUrl_InvalidUrl_ThrowsNamingTheSetting(string configured)
    {
        var act = () => AudiobookshelfServer.ResolveConfiguredUrl(configured);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{AudiobookshelfServer.UrlConfigKey}*");
    }
}
