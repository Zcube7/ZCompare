using ZCompare.App.Infrastructure;

namespace ZCompare.App.Tests;

public sealed class LruCacheTests
{
    [Fact]
    public void SetEvictsLeastRecentlyUsedEntry()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("first", 1);
        cache.Set("second", 2);

        Assert.True(cache.TryGetValue("first", out var first));
        Assert.Equal(1, first);

        cache.Set("third", 3);

        Assert.False(cache.TryGetValue("second", out _));
        Assert.True(cache.TryGetValue("first", out var retained));
        Assert.Equal(1, retained);
        Assert.True(cache.TryGetValue("third", out var newest));
        Assert.Equal(3, newest);
    }
}
