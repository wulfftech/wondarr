using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Logging;
using Wondarr.Sources.Torznab;
using Wondarr.Sources.Torznab.Indexers;
using Xunit;

namespace Wondarr.Sources.Tests.Torznab.Indexers;

/// <summary>
/// The NZB cache that lets a grab reuse the NZB a search downloaded: what it keeps, what it evicts
/// (by count, by total size, by age) and that parallel reads and writes cannot break those bounds.
/// </summary>
public sealed class NzbCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Is_bounded_to_64_nzbs_and_16_mib_for_6_hours_by_default()
    {
        NzbCache.DefaultMaxEntries.Should().Be(64);
        NzbCache.DefaultMaxBytes.Should().Be(16L * 1024 * 1024);
        NzbCache.DefaultLifetime.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void The_host_registers_one_cache_for_every_search_and_grab()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRegistry>(new SecretRegistry());
        services.AddLogging();
        services.AddWondarrTorznab();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NzbCache>().Should().BeSameAs(provider.GetRequiredService<NzbCache>());
    }

    [Fact]
    public void Gives_back_what_was_put_and_nothing_for_another_key()
    {
        var cache = new NzbCache(new FakeTimeProvider(Start));
        var nzb = Bytes(10);

        cache.Put("a", nzb);

        cache.TryGet("a", out var found).Should().BeTrue();
        found.Should().BeSameAs(nzb, "the bytes are kept, not copied");
        cache.TryGet("b", out var missing).Should().BeFalse();
        missing.Should().BeNull();
    }

    [Fact]
    public void Evicts_the_oldest_when_there_are_more_entries_than_allowed()
    {
        var cache = new NzbCache(new FakeTimeProvider(Start), maxEntries: 3, maxBytes: 1_000_000, lifetime: TimeSpan.FromHours(6));

        foreach (var key in new[] { "a", "b", "c", "d" })
        {
            cache.Put(key, Bytes(10));
        }

        cache.Count.Should().Be(3);
        cache.TryGet("a", out _).Should().BeFalse("it was the oldest");
        cache.TryGet("b", out _).Should().BeTrue();
        cache.TryGet("c", out _).Should().BeTrue();
        cache.TryGet("d", out _).Should().BeTrue();
        cache.Bytes.Should().Be(30);
    }

    [Fact]
    public void Evicts_the_oldest_until_the_total_size_fits()
    {
        var cache = new NzbCache(new FakeTimeProvider(Start), maxEntries: 10, maxBytes: 100, lifetime: TimeSpan.FromHours(6));

        cache.Put("a", Bytes(40));
        cache.Put("b", Bytes(40));
        cache.Put("c", Bytes(40));

        cache.TryGet("a", out _).Should().BeFalse("120 bytes do not fit in 100; the oldest went");
        cache.Bytes.Should().Be(80);

        cache.Put("d", Bytes(90));

        cache.Count.Should().Be(1, "90 bytes leave no room for anything older");
        cache.TryGet("d", out _).Should().BeTrue();
        cache.Bytes.Should().Be(90);
    }

    [Fact]
    public void An_nzb_bigger_than_the_whole_cache_is_not_kept_and_evicts_nothing()
    {
        var cache = new NzbCache(new FakeTimeProvider(Start), maxEntries: 10, maxBytes: 100, lifetime: TimeSpan.FromHours(6));
        cache.Put("a", Bytes(60));

        cache.Put("huge", Bytes(101));

        cache.TryGet("huge", out _).Should().BeFalse();
        cache.TryGet("a", out _).Should().BeTrue();
        cache.Bytes.Should().Be(60);
    }

    [Fact]
    public void Putting_a_key_again_replaces_it_counts_its_size_once_and_makes_it_the_youngest()
    {
        var cache = new NzbCache(new FakeTimeProvider(Start), maxEntries: 2, maxBytes: 1_000, lifetime: TimeSpan.FromHours(6));
        cache.Put("a", Bytes(10));
        cache.Put("b", Bytes(10));

        cache.Put("a", Bytes(25));
        cache.Put("c", Bytes(10));

        cache.Count.Should().Be(2);
        cache.TryGet("b", out _).Should().BeFalse("b was the oldest once a was put again");
        cache.TryGet("a", out var replaced).Should().BeTrue();
        replaced!.Length.Should().Be(25);
        cache.Bytes.Should().Be(35);
    }

    [Fact]
    public void An_entry_expires_after_six_hours()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new NzbCache(time);
        cache.Put("a", Bytes(10));

        time.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        cache.TryGet("a", out _).Should().BeTrue("six hours have not passed yet");

        time.Advance(TimeSpan.FromSeconds(1));
        cache.TryGet("a", out _).Should().BeFalse();
        cache.Count.Should().Be(0);
        cache.Bytes.Should().Be(0);
    }

    [Fact]
    public void Only_the_expired_entries_go_and_a_put_again_starts_a_new_lifetime()
    {
        var time = new FakeTimeProvider(Start);
        var cache = new NzbCache(time);
        cache.Put("old", Bytes(10));
        cache.Put("renewed", Bytes(10));
        time.Advance(TimeSpan.FromHours(4));
        cache.Put("renewed", Bytes(10));
        cache.Put("young", Bytes(10));

        time.Advance(TimeSpan.FromHours(3));

        cache.TryGet("old", out _).Should().BeFalse("it is 7 hours old");
        cache.TryGet("renewed", out _).Should().BeTrue("it was put again 3 hours ago");
        cache.TryGet("young", out _).Should().BeTrue();
        cache.Count.Should().Be(2);
        cache.Bytes.Should().Be(20);
    }

    [Fact]
    public void The_key_depends_on_the_indexer_and_the_download_link_and_does_not_contain_the_link()
    {
        const string link = "https://dog.example/getnzb/abc.nzb&i=1&r=S3cretIndexerKey";

        var key = NzbCache.KeyOf(1, link, "guid-1");

        key.Should().NotContain("S3cretIndexerKey").And.NotContain("dog.example");
        NzbCache.KeyOf(1, link, "guid-1").Should().Be(key, "the same release always gets the same key");
        NzbCache.KeyOf(1, link, "guid-2").Should().Be(key, "the link identifies the download");
        NzbCache.KeyOf(2, link, "guid-1").Should().NotBe(key);
        NzbCache.KeyOf(1, link + "2", "guid-1").Should().NotBe(key);
    }

    [Fact]
    public void A_guid_that_equals_some_release_s_link_does_not_collide_with_that_release()
    {
        const string link = "https://dog.example/getnzb/abc.nzb";

        NzbCache.KeyOf(1, null, link).Should().NotBe(NzbCache.KeyOf(1, link, "guid-1"), "the first is keyed by a guid, the second by a link");
        NzbCache.KeyOf(1, " ", link).Should().NotBe(NzbCache.KeyOf(1, link, link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_release_without_a_link_is_keyed_by_its_guid(string? link)
    {
        NzbCache.KeyOf(1, link, "guid-1").Should().NotBe(NzbCache.KeyOf(1, link, "guid-2"));
        NzbCache.KeyOf(1, link, "guid-1").Should().Be(NzbCache.KeyOf(1, null, "guid-1"));
    }

    [Fact]
    public void Parallel_puts_and_gets_keep_the_count_and_size_bounds_and_the_books_balanced()
    {
        const int maxEntries = 16;
        const long maxBytes = 20_000;
        const int keyCount = 200;
        var cache = new NzbCache(new FakeTimeProvider(Start), maxEntries, maxBytes, TimeSpan.FromHours(6));
        var overshoot = 0;

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            var random = new Random(worker);

            for (var step = 0; step < 2_000; step++)
            {
                var key = "k" + random.Next(keyCount);

                if (random.Next(3) == 0)
                {
                    cache.TryGet(key, out _);
                }
                else
                {
                    cache.Put(key, Bytes(random.Next(1, 4_000)));
                }

                if (cache.Count > maxEntries || cache.Bytes > maxBytes)
                {
                    Interlocked.Increment(ref overshoot);
                }
            }
        });

        overshoot.Should().Be(0, "no reader ever saw the cache over a bound");
        cache.Count.Should().BeLessThanOrEqualTo(maxEntries);
        cache.Bytes.Should().BeLessThanOrEqualTo(maxBytes);

        var found = 0;
        long bytes = 0;

        for (var key = 0; key < keyCount; key++)
        {
            if (cache.TryGet("k" + key, out var nzb))
            {
                found++;
                bytes += nzb!.Length;
            }
        }

        found.Should().Be(cache.Count, "every counted entry can be found, and nothing else");
        bytes.Should().Be(cache.Bytes, "the byte total matches what the entries hold");
    }

    private static byte[] Bytes(int length) => new byte[length];
}
