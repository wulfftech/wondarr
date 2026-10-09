using Wondarr.Core.Authentication;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Authentication;

/// <summary>
/// An in-memory settings repository that holds the very first read until a second read arrives
/// (or a timeout passes), to force the interleaving a check-then-write race needs.
/// </summary>
internal sealed class GatedSettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, object> _values = [];
    private readonly object _sync = new();
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _hold;
    private int _reads;

    public GatedSettingsRepository(TimeSpan hold) => _hold = hold;

    public Task FirstReadStarted => _firstRead.Task;

    public int Writes { get; private set; }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        int number;
        lock (_sync)
        {
            number = ++_reads;
        }

        if (number == 1)
        {
            _firstRead.TrySetResult();
            await Task.WhenAny(_secondRead.Task, Task.Delay(_hold, cancellationToken)).ConfigureAwait(false);
        }
        else if (number == 2)
        {
            _secondRead.TrySetResult();
        }

        lock (_sync)
        {
            return _values.TryGetValue(key, out var value) ? (T)value : default;
        }
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _values[key] = value!;
            Writes++;
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return Task.FromResult(_values.Remove(key));
        }
    }
}

public class CredentialStoreTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task Verifies_the_right_password_and_rejects_a_wrong_one()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var store = new CredentialStore(new SettingsRepository(context));

        (await store.IsConfiguredAsync(CancellationToken.None)).Should().BeFalse();
        (await store.GetUsernameAsync(CancellationToken.None)).Should().BeNull();

        await store.SetAsync("admin", Password, CancellationToken.None);

        (await store.IsConfiguredAsync(CancellationToken.None)).Should().BeTrue();
        (await store.GetUsernameAsync(CancellationToken.None)).Should().Be("admin");
        (await store.VerifyAsync("admin", Password, CancellationToken.None)).Should().BeTrue();
        (await store.VerifyAsync("ADMIN", Password, CancellationToken.None)).Should().BeTrue();
        (await store.VerifyAsync("admin", "wrong password here", CancellationToken.None)).Should().BeFalse();
        (await store.VerifyAsync("someone-else", Password, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Stores_the_password_only_as_a_salted_hash()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var store = new CredentialStore(new SettingsRepository(context));

        await store.SetAsync("admin", Password, CancellationToken.None);

        var stored = await context.Settings.AsNoTracking().Select(row => row.Value).SingleAsync();

        stored.Should().Contain("salt").And.Contain("iterations").And.Contain("hash");
        stored.Should().NotContain(Password);
        stored.Should().NotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Password)));
    }

    [Fact]
    public async Task A_second_SetAsync_replaces_the_credentials()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var store = new CredentialStore(new SettingsRepository(context));

        await store.SetAsync("admin", Password, CancellationToken.None);
        await store.SetAsync("someone-else", "a different password", CancellationToken.None);

        context.Settings.Should().HaveCount(1);
        (await store.GetUsernameAsync(CancellationToken.None)).Should().Be("someone-else");
        (await store.VerifyAsync("admin", Password, CancellationToken.None)).Should().BeFalse();
        (await store.VerifyAsync("someone-else", "a different password", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_an_unknown_username_even_with_the_right_password()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var store = new CredentialStore(new SettingsRepository(context));
        await store.SetAsync("admin", Password, CancellationToken.None);

        (await store.VerifyAsync("someone", Password, CancellationToken.None)).Should().BeFalse();
        (await store.VerifyAsync("ADMIN", Password, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task TrySetInitialAsync_creates_the_credentials_once_and_then_refuses()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var store = new CredentialStore(new SettingsRepository(context));

        (await store.TrySetInitialAsync("admin", Password, CancellationToken.None)).Should().BeTrue();
        (await store.TrySetInitialAsync("someone-else", "a different password", CancellationToken.None)).Should().BeFalse();

        (await store.GetUsernameAsync(CancellationToken.None)).Should().Be("admin");
        (await store.VerifyAsync("admin", Password, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Two_overlapping_TrySetInitialAsync_calls_let_exactly_one_win_even_when_both_read_first()
    {
        // The first reader is held until the second has also asked, so without a lock both would
        // see "no credentials" and both would write. With the lock the second asks only after the
        // first has finished, so the hold times out and the first proceeds alone.
        var repository = new GatedSettingsRepository(TimeSpan.FromMilliseconds(500));
        var firstStore = new CredentialStore(repository);
        var secondStore = new CredentialStore(repository);

        var results = await Task.WhenAll(
            Task.Run(() => firstStore.TrySetInitialAsync("first", Password, CancellationToken.None)),
            Task.Run(() => secondStore.TrySetInitialAsync("second", Password, CancellationToken.None)));

        results.Count(won => won).Should().Be(1);
        repository.Writes.Should().Be(1);
    }

    [Fact]
    public async Task SetAsync_waits_for_an_initial_creation_in_progress()
    {
        var repository = new GatedSettingsRepository(TimeSpan.FromMilliseconds(500));
        var store = new CredentialStore(repository);

        var initial = Task.Run(() => store.TrySetInitialAsync("first", Password, CancellationToken.None));
        await repository.FirstReadStarted;
        var replace = Task.Run(() => store.SetAsync("replacement", Password, CancellationToken.None));

        (await initial).Should().BeTrue();
        await replace;

        // The replace ran after the creation finished, never between its check and its write.
        repository.Writes.Should().Be(2);
        (await store.GetUsernameAsync(CancellationToken.None)).Should().Be("replacement");
    }

    [Fact]
    public async Task Two_concurrent_TrySetInitialAsync_calls_let_exactly_one_win()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        // Separate contexts, as two simultaneous requests would have.
        await using var first = database.CreateContext(timeProvider);
        await using var second = database.CreateContext(timeProvider);
        var firstStore = new CredentialStore(new SettingsRepository(first));
        var secondStore = new CredentialStore(new SettingsRepository(second));

        var results = await Task.WhenAll(
            Task.Run(() => firstStore.TrySetInitialAsync("first", Password, CancellationToken.None)),
            Task.Run(() => secondStore.TrySetInitialAsync("second", Password, CancellationToken.None)));

        results.Count(won => won).Should().Be(1);

        await using var check = database.CreateContext(timeProvider);
        (await check.Settings.CountAsync()).Should().Be(1);
    }
}
