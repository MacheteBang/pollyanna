using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>
/// Linking by email (spec §3): an unknown login with an explicitly verified email that matches
/// exactly one existing person is added to that person, next to their other logins, instead of
/// creating a new person.
/// </summary>
public sealed class RelinkTests : IAsyncLifetime
{
    const string OldIssuer = "https://old-provider.test";
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    async Task<Guid> SeedPersonAsync(string sub, string? email, string issuer = OldIssuer)
    {
        var person = new Person
        {
            Logins = [new Login { Issuer = issuer, Subject = sub, LastEmail = email, CreatedAt = DateTime.UtcNow, LastUsedAt = DateTime.UtcNow }],
            DisplayName = "Seeded " + sub,
            Email = email,
            EmailNormalized = Person.NormalizeEmail(email),
            EmailVerified = true,
            CreatedAt = DateTime.UtcNow,
        };
        await app.WithDbAsync(async db => { db.People.Add(person); await db.SaveChangesAsync(); });
        return person.Id;
    }

    async Task<Person> SignInAndGetPersonAsync(MockIdentity identity)
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(identity);
        return await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == db.Logins
            .Where(l => l.Issuer == TestApp.Issuer && l.Subject == identity.Sub).Select(l => l.PersonId).Single()));
    }

    Task<List<(string Issuer, string Sub)>> LoginsAsync(Guid person) =>
        app.WithDbAsync(async db => (await db.Logins.Where(l => l.PersonId == person).OrderBy(l => l.Issuer).ThenBy(l => l.Subject)
            .Select(l => new { l.Issuer, l.Subject }).ToListAsync()).Select(l => (l.Issuer, l.Subject)).ToList());

    [Fact]
    public async Task Verified_email_adds_a_login_to_the_matching_person_and_keeps_their_old_one()
    {
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");
        var bob = await SeedPersonAsync("bob-old", "bob@example.com");

        var person = await SignInAndGetPersonAsync(new("carol-new", "Carol", "carol@example.com", true, null));

        Assert.Equal(carol, person.Id);
        Assert.Equal([(TestApp.Issuer, "carol-new"), (OldIssuer, "carol-old")], await LoginsAsync(carol));
        Assert.Equal(2, await app.WithDbAsync(db => db.People.CountAsync()));
        // Bob is untouched.
        Assert.Equal([(OldIssuer, "bob-old")], await LoginsAsync(bob));
        Assert.Contains(app.Logs.Entries, e => e.Message == $"Added a login to person {carol} by verified email.");
    }

    [Fact]
    public async Task The_earlier_login_still_signs_in_after_a_login_is_added()
    {
        var carol = await SeedPersonAsync("carol-first", "carol@example.com", TestApp.Issuer);
        await SignInAndGetPersonAsync(new("carol-second", "Carol", "carol@example.com", true, null));

        var again = await SignInAndGetPersonAsync(new("carol-first", "Carol", "carol@example.com", true, null));

        Assert.Equal(carol, again.Id);
        Assert.Equal([(TestApp.Issuer, "carol-first"), (TestApp.Issuer, "carol-second")], await LoginsAsync(carol));
    }

    [Fact]
    public async Task Linking_also_covers_a_recreated_account_at_the_same_provider()
    {
        var carol = await SeedPersonAsync("carol-deleted", "carol@example.com", TestApp.Issuer);
        var person = await SignInAndGetPersonAsync(new("carol-recreated", "Carol", "carol@example.com", true, null));
        Assert.Equal(carol, person.Id);
    }

    [Fact]
    public async Task Email_matching_ignores_case()
    {
        var carol = await SeedPersonAsync("carol-old", "Carol@Example.com");
        var person = await SignInAndGetPersonAsync(new("carol-new", "Carol", "carol@EXAMPLE.com", true, null));
        Assert.Equal(carol, person.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)] // no email_verified claim at all
    public async Task Email_not_explicitly_verified_never_links(bool? verified)
    {
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");
        var person = await SignInAndGetPersonAsync(new("impostor", "Carol?", "carol@example.com", verified, null));

        Assert.NotEqual(carol, person.Id);
        Assert.Equal([(OldIssuer, "carol-old")], await LoginsAsync(carol));
    }

    [Fact]
    public async Task Email_matching_more_than_one_person_links_nobody_and_creates_a_new_person()
    {
        var a = await SeedPersonAsync("shared-1", "family@example.com");
        var b = await SeedPersonAsync("shared-2", "family@example.com");

        var person = await SignInAndGetPersonAsync(new("someone-new", "New", "family@example.com", true, null));

        Assert.NotEqual(a, person.Id);
        Assert.NotEqual(b, person.Id);
        Assert.Equal(3, await app.WithDbAsync(db => db.People.CountAsync()));
        Assert.Equal([(OldIssuer, "shared-1")], await LoginsAsync(a));
        Assert.Equal([(OldIssuer, "shared-2")], await LoginsAsync(b));

        // One line with the count, naming nobody.
        var line = Assert.Single(app.Logs.Entries, e => e.Message.Contains("matches 2 people")).Message;
        Assert.DoesNotContain("family@", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.ToString(), line);
        Assert.DoesNotContain(b.ToString(), line);
        Assert.DoesNotContain(person.Id.ToString(), line);
        Assert.DoesNotContain("someone-new", line);
    }

    [Fact]
    public async Task A_tombstone_never_counts_as_a_match()
    {
        // A tombstone has no email, but even one left with an email (and no logins) never matches.
        var gone = await SeedPersonAsync("gone", "carol@example.com");
        await app.WithDbAsync(db => db.Logins.Where(l => l.PersonId == gone).ExecuteDeleteAsync());
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");

        var person = await SignInAndGetPersonAsync(new("carol-new", "Carol", "carol@example.com", true, null));

        Assert.Equal(carol, person.Id);
        Assert.Empty(await LoginsAsync(gone));
    }

    [Fact]
    public async Task A_known_account_is_never_relinked_elsewhere()
    {
        // Dave's own account keeps mapping to Dave even if his email now matches Carol's.
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");
        var dave = await SeedPersonAsync("dave", "dave@example.com", TestApp.Issuer);

        var person = await SignInAndGetPersonAsync(new("dave", "Dave", "carol@example.com", true, null));

        Assert.Equal(dave, person.Id);
        Assert.Equal([(OldIssuer, "carol-old")], await LoginsAsync(carol));
        Assert.Equal([(TestApp.Issuer, "dave")], await LoginsAsync(dave));
    }

    [Fact]
    public async Task A_person_with_an_added_login_keeps_their_event_history()
    {
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");
        var ev = new Event { Name = "Stockings 2025", ExchangeDate = new DateOnly(2025, 12, 24), Status = EventStatus.Open, CreatedBy = carol, CreatedAt = DateTime.UtcNow };
        ev.Participants.Add(new Participant { PersonId = carol, JoinedAt = DateTime.UtcNow });
        await app.WithDbAsync(async db => { db.Events.Add(ev); await db.SaveChangesAsync(); });

        using var browser = app.NewBrowser();
        await browser.SignInAsync(new("carol-new", "Carol", "carol@example.com", true, null));
        var home = await browser.GetPageAsync("/");
        Assert.Contains("Stockings 2025", home.Body!.TextContent);
    }

    [Fact]
    public async Task A_provider_without_email_verified_is_noted_once_naming_nobody()
    {
        await SignInAndGetPersonAsync(new MockIdentity("u1", "Uma", "uma@example.com", true, null));
        Assert.DoesNotContain(app.Logs.Entries, e => e.Message.Contains("no email_verified claim"));

        await SignInAndGetPersonAsync(new MockIdentity("u2", "Vic", "vic@example.com", null, null));
        await SignInAndGetPersonAsync(new MockIdentity("u3", "Wes", "wes@example.com", null, null));

        var hint = Assert.Single(app.Logs.Entries, e => e.Message.Contains("no email_verified claim")).Message;
        Assert.Contains("Invitations by email still match", hint);
        Assert.Contains("adding a login to someone by verified email won't happen", hint);
        Assert.DoesNotContain("re-linking", hint);
        Assert.DoesNotContain("vic", hint, StringComparison.OrdinalIgnoreCase);
    }
}
