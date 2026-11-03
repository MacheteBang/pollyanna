using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>
/// Re-linking (spec §3): an unknown issuer/sub with an explicitly verified email that matches
/// exactly one existing person attaches to that person instead of creating a new one.
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
            OidcIssuer = issuer,
            OidcSub = sub,
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
        return await app.WithDbAsync(db => db.People.SingleAsync(p => p.OidcIssuer == TestApp.Issuer && p.OidcSub == identity.Sub));
    }

    [Fact]
    public async Task Verified_email_relinks_to_the_matching_person_after_a_provider_switch()
    {
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");
        var bob = await SeedPersonAsync("bob-old", "bob@example.com");

        var person = await SignInAndGetPersonAsync(new("carol-new", "Carol", "carol@example.com", true, null));

        Assert.Equal(carol, person.Id);
        Assert.Equal(TestApp.Issuer, person.OidcIssuer);
        Assert.Equal("carol-new", person.OidcSub);
        Assert.Equal(2, await app.WithDbAsync(db => db.People.CountAsync()));
        // Bob is untouched.
        var bobRow = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == bob));
        Assert.Equal(("bob-old", OldIssuer), (bobRow.OidcSub, bobRow.OidcIssuer));
    }

    [Fact]
    public async Task Relinking_also_covers_a_recreated_account_at_the_same_provider()
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
    public async Task Email_not_explicitly_verified_never_relinks(bool? verified)
    {
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");
        var person = await SignInAndGetPersonAsync(new("impostor", "Carol?", "carol@example.com", verified, null));

        Assert.NotEqual(carol, person.Id);
        var original = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == carol));
        Assert.Equal(("carol-old", OldIssuer), (original.OidcSub, original.OidcIssuer));
    }

    [Fact]
    public async Task Email_matching_more_than_one_person_creates_a_new_person()
    {
        var a = await SeedPersonAsync("shared-1", "family@example.com");
        var b = await SeedPersonAsync("shared-2", "family@example.com");

        var person = await SignInAndGetPersonAsync(new("someone-new", "New", "family@example.com", true, null));

        Assert.NotEqual(a, person.Id);
        Assert.NotEqual(b, person.Id);
        Assert.Equal(3, await app.WithDbAsync(db => db.People.CountAsync()));
    }

    [Fact]
    public async Task A_known_account_is_never_relinked_elsewhere()
    {
        // Dave's own account keeps mapping to Dave even if his email now matches Carol's.
        var carol = await SeedPersonAsync("carol-old", "carol@example.com");
        var dave = await SeedPersonAsync("dave", "dave@example.com", TestApp.Issuer);

        var person = await SignInAndGetPersonAsync(new("dave", "Dave", "carol@example.com", true, null));

        Assert.Equal(dave, person.Id);
        var carolRow = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == carol));
        Assert.Equal("carol-old", carolRow.OidcSub);
    }

    [Fact]
    public async Task Relinked_person_keeps_their_event_history()
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
        Assert.Contains("re-linking a recreated account by email won't work", hint);
        Assert.DoesNotContain("vic", hint, StringComparison.OrdinalIgnoreCase);
    }
}
