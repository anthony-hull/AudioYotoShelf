using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AudioYotoShelf.Infrastructure.Tests;

public class SsoFlowStoreTests
{
    private static readonly TimeSpan LongEnough = TimeSpan.FromMinutes(5);
    private const string State = "state-1";
    private const string BrowserNonce = "nonce-of-the-browser-that-started-it";

    private readonly IDistributedCache _cache = new MemoryDistributedCache(
        Options.Create(new MemoryDistributedCacheOptions()));
    private readonly DistributedCacheSsoFlowStore _sut;

    public SsoFlowStoreTests() => _sut = new DistributedCacheSsoFlowStore(_cache);

    private static PendingSsoFlow Flow(string nonce = BrowserNonce) => new(
        CodeVerifier: "verifier-1",
        AbsCookies: new Dictionary<string, string> { ["connect.sid"] = "s%3Aabc", ["auth_method"] = "openid-mobile" },
        BrowserNonce: nonce);

    [Fact]
    public async Task Take_ReturnsWhatWasSaved_ForTheBrowserThatStartedIt()
    {
        await _sut.SaveAsync(State, Flow(), LongEnough);

        var taken = await _sut.TakeAsync(State, BrowserNonce);

        taken.Should().NotBeNull();
        taken!.CodeVerifier.Should().Be("verifier-1");
        taken.AbsCookies.Should().Equal(Flow().AbsCookies);
    }

    [Fact]
    public async Task Take_UnknownState_ReturnsNull()
    {
        var taken = await _sut.TakeAsync("never-saved", BrowserNonce);

        taken.Should().BeNull();
    }

    [Fact]
    public async Task Take_SecondTime_ReturnsNull_SoACompletedFlowCannotBeReplayed()
    {
        await _sut.SaveAsync(State, Flow(), LongEnough);
        await _sut.TakeAsync(State, BrowserNonce);

        var replay = await _sut.TakeAsync(State, BrowserNonce);

        replay.Should().BeNull();
    }

    [Fact]
    public async Task Take_AfterItExpires_ReturnsNull()
    {
        await _sut.SaveAsync(State, Flow(), TimeSpan.FromMilliseconds(50));
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var taken = await _sut.TakeAsync(State, BrowserNonce);

        taken.Should().BeNull();
    }

    [Theory]
    [InlineData("a-different-browsers-nonce")]
    [InlineData(null)]
    [InlineData("")]
    public async Task Take_FromABrowserThatDidNotStartIt_ReturnsNull(string? otherNonce)
    {
        // Login CSRF: an attacker starts a flow in their own browser and tricks a victim's browser
        // into finishing it, which would sign the victim in as the attacker.
        await _sut.SaveAsync(State, Flow(), LongEnough);

        var taken = await _sut.TakeAsync(State, otherNonce);

        taken.Should().BeNull();
    }

    [Fact]
    public async Task Take_FromTheWrongBrowser_StillUsesUpTheState()
    {
        await _sut.SaveAsync(State, Flow(), LongEnough);
        await _sut.TakeAsync(State, "a-different-browsers-nonce");

        var rightBrowserAfterwards = await _sut.TakeAsync(State, BrowserNonce);

        rightBrowserAfterwards.Should().BeNull();
    }

    [Fact]
    public async Task Save_TwoFlows_DoNotCollide()
    {
        await _sut.SaveAsync("state-a", Flow("nonce-a") with { CodeVerifier = "verifier-a" }, LongEnough);
        await _sut.SaveAsync("state-b", Flow("nonce-b") with { CodeVerifier = "verifier-b" }, LongEnough);

        var b = await _sut.TakeAsync("state-b", "nonce-b");
        var a = await _sut.TakeAsync("state-a", "nonce-a");

        (a!.CodeVerifier, b!.CodeVerifier).Should().Be(("verifier-a", "verifier-b"));
    }
}
