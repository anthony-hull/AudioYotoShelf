using AudioYotoShelf.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class RedisCacheServiceTests
{
    private readonly RedisCacheService _sut;
    private readonly IDistributedCache _cache;

    public RedisCacheServiceTests()
    {
        // Use MemoryDistributedCache as a test double for Redis
        _cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _sut = new RedisCacheService(_cache, Mock.Of<ILogger<RedisCacheService>>());
    }

    private record TestItem(string Name, int Value);

    [Fact]
    public async Task GetAsync_NonExistentKey_ReturnsNull()
    {
        var result = await _sut.GetAsync<TestItem>("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        var item = new TestItem("hello", 42);
        await _sut.SetAsync("test-key", item);

        var result = await _sut.GetAsync<TestItem>("test-key");
        result.Should().NotBeNull();
        result!.Name.Should().Be("hello");
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task RemoveAsync_DeletesKey()
    {
        await _sut.SetAsync("to-delete", new TestItem("bye", 0));
        await _sut.RemoveAsync("to-delete");

        var result = await _sut.GetAsync<TestItem>("to-delete");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOrSetAsync_CacheMiss_CallsFactory()
    {
        var factoryCalled = false;
        var result = await _sut.GetOrSetAsync("factory-key", async _ =>
        {
            factoryCalled = true;
            return new TestItem("from-factory", 99);
        });

        factoryCalled.Should().BeTrue();
        result.Name.Should().Be("from-factory");
    }

    [Fact]
    public async Task GetOrSetAsync_CacheHit_DoesNotCallFactory()
    {
        await _sut.SetAsync("pre-set", new TestItem("cached", 1));

        var factoryCalled = false;
        var result = await _sut.GetOrSetAsync("pre-set", async _ =>
        {
            factoryCalled = true;
            return new TestItem("should-not-use", 999);
        });

        factoryCalled.Should().BeFalse();
        result.Name.Should().Be("cached");
    }

    [Fact]
    public async Task GetOrSetAsync_FactoryResultIsCachedForSubsequentCalls()
    {
        var callCount = 0;
        async Task<TestItem> Factory(CancellationToken ct)
        {
            callCount++;
            return new TestItem($"call-{callCount}", callCount);
        }

        var first = await _sut.GetOrSetAsync("count-key", Factory);
        var second = await _sut.GetOrSetAsync("count-key", Factory);

        callCount.Should().Be(1);
        first.Name.Should().Be("call-1");
        second.Name.Should().Be("call-1");
    }

    [Fact]
    public async Task SetAsync_WithTtl_ValueExpires()
    {
        // Note: MemoryDistributedCache uses sliding expiration internally.
        // This test verifies TTL is passed without error; true expiration
        // requires a real Redis instance (integration test).
        await _sut.SetAsync("ttl-key", new TestItem("ephemeral", 0), TimeSpan.FromMinutes(5));

        var result = await _sut.GetAsync<TestItem>("ttl-key");
        result.Should().NotBeNull();
    }

    // =========================================================================
    // CacheKeys tests
    // =========================================================================

    [Fact]
    public void CacheKeys_AbsLibraries_IncludesUserId()
    {
        var key = CacheKeys.AbsLibraries("user-1");
        key.Should().Be("abs:libraries:user-1");
    }

    [Fact]
    public void CacheKeys_AbsLibraryItems_IncludesAllSegments()
    {
        var key = CacheKeys.AbsLibraryItems("user-1", "lib-1", 3);
        key.Should().Be("abs:items:user-1:lib-1:3");
    }

    [Fact]
    public void CacheKeys_GeminiDailyCount_IncludesDate()
    {
        var key = CacheKeys.GeminiDailyCount(new DateOnly(2026, 3, 1));
        key.Should().Be("gemini:count:2026-03-01");
    }

    // =========================================================================
    // Mutation-testing additions
    // =========================================================================

    [Fact]
    public async Task SetAsync_StoresCompactCamelCaseJson()
    {
        await _sut.SetAsync("json-key", new TestItem("hello", 42));

        (await _cache.GetStringAsync("json-key")).Should().Be("""{"name":"hello","value":42}""");
    }

    [Fact]
    public async Task SetAsync_ExplicitTtl_IsUsedAsTheAbsoluteExpiry()
    {
        var cache = new Mock<IDistributedCache>();
        var sut = new RedisCacheService(cache.Object, Mock.Of<ILogger<RedisCacheService>>());

        await sut.SetAsync("k", new TestItem("x", 1), TimeSpan.FromMinutes(3));

        cache.Verify(c => c.SetAsync("k", It.IsAny<byte[]>(),
            It.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromMinutes(3)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAsync_NoTtl_ExpiresAfterTenMinutes()
    {
        var cache = new Mock<IDistributedCache>();
        var sut = new RedisCacheService(cache.Object, Mock.Of<ILogger<RedisCacheService>>());

        await sut.SetAsync("k", new TestItem("x", 1));

        cache.Verify(c => c.SetAsync("k", It.IsAny<byte[]>(),
            It.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromMinutes(10)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A cache outage must degrade to "not cached", never take the request down with it.

    private static RedisCacheService ServiceOverABrokenCache()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        cache.Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        return new RedisCacheService(cache.Object, Mock.Of<ILogger<RedisCacheService>>());
    }

    [Fact]
    public async Task GetAsync_CacheFails_ReturnsNull()
    {
        (await ServiceOverABrokenCache().GetAsync<TestItem>("k")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_CacheFails_DoesNotThrow()
    {
        var act = () => ServiceOverABrokenCache().SetAsync("k", new TestItem("x", 1));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveAsync_CacheFails_DoesNotThrow()
    {
        var act = () => ServiceOverABrokenCache().RemoveAsync("k");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetOrSetAsync_CacheFails_StillReturnsTheFactoryValue()
    {
        var result = await ServiceOverABrokenCache().GetOrSetAsync("k", _ => Task.FromResult(new TestItem("fresh", 7)));

        result.Should().Be(new TestItem("fresh", 7));
    }

    [Fact]
    public void CacheKeys_AbsItem_IncludesTheItemId() =>
        CacheKeys.AbsItem("item-9").Should().Be("abs:item:item-9");

    [Fact]
    public void CacheKeys_AbsSeries_IncludesLibraryAndPage() =>
        CacheKeys.AbsSeries("lib-1", 2).Should().Be("abs:series:lib-1:2");
}
