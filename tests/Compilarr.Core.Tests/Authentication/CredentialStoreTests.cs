using Compilarr.Core.Authentication;
using Compilarr.Core.Persistence;
using Compilarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.Authentication;

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
}
