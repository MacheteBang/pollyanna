using System.Net;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>
/// An open provider lets in only a verified email that matches one person or a pending
/// invitation; anyone else lands on the refused page with nothing stored (spec §3, §4).
/// </summary>
public sealed class OpenProviderTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() =>
        app = await TestApp.StartAsync(providers: [new("google", "Google", "open", TestImages.Svg), new("work", "Work account")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    static readonly MockIdentity Sue = new("sue-google", "Sue Example", "Sue@Example.com", true, null);

    /// <summary>Signs in through Google and returns where Pollyanna sent the browser afterwards.</summary>
    async Task<(TestBrowser Browser, string Location)> SignInThroughGoogleAsync(MockIdentity identity)
    {
        var browser = app.NewBrowser();
        var callback = await browser.SignInAsync(identity, "/account", provider: "google");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return (browser, callback.Headers.Location!.OriginalString);
    }

    async Task<IDocument> RefusedPageAsync(MockIdentity identity)
    {
        var (browser, location) = await SignInThroughGoogleAsync(identity);
        using var _ = browser;
        Assert.StartsWith("/auth/failed", location);
        var page = await browser.GetPageAsync(location);
        // Refused means not signed in: the page asked for still sends the browser to sign in.
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/account")).StatusCode);
        return page;
    }

    /// <summary>An event (by a seeded organizer) with a pending invitation to <paramref name="email"/>.</summary>
    async Task<Guid> InviteAsync(string email, EventStatus status = EventStatus.Open)
    {
        var org = await app.PersonAsync("Org", "org-" + Guid.NewGuid() + "@example.com");
        var ev = await app.EventAsync("Stockings", status, [org], [org]);
        await app.WithDbAsync(async db =>
        {
            db.Invitations.Add(new Invitation { EventId = ev, Email = email, EmailNormalized = email.ToLowerInvariant(), InvitedBy = org, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        return ev;
    }

    Task<int> PeopleAsync() => app.WithDbAsync(db => db.People.CountAsync());
    Task<int> LoginsAsync() => app.WithDbAsync(db => db.Logins.CountAsync());
    Task<int> SessionsAsync() => app.WithDbAsync(db => db.Sessions.CountAsync());

    /// <summary>The refused page's heading, its text, and its buttons (label, href, outlined).</summary>
    static (string Heading, string Text, List<(string Label, string? Href, bool Outlined)> Buttons) Read(IDocument page) =>
    (
        page.QuerySelector(".empty h1")!.TextContent.Trim(),
        string.Join(" ", page.QuerySelectorAll(".empty p").Select(p => p.TextContent.Trim())),
        [.. page.QuerySelectorAll(".actions .button").Select(a => (a.TextContent.Trim(), a.GetAttribute("href"), a.ClassList.Contains("button-outline")))]
    );

    void AssertRefusalLogged(string reason)
    {
        Assert.Contains(app.Logs.Entries, e => e.Category.StartsWith("Pollyanna.Auth")
            && e.Message == $"Refused a sign-in through provider google: {reason}.");
        Assert.DoesNotContain(app.Logs.Entries, e => e.Message.Contains("sue@example.com", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains(Sue.Sub));
    }

    async Task AssertNothingStoredAsync(int people, int logins, int sessions)
    {
        Assert.Equal(people, await PeopleAsync());
        Assert.Equal(logins, await LoginsAsync());
        Assert.Equal(sessions, await SessionsAsync());
    }

    static readonly List<(string, string?, bool)> EveryWayIn =
    [
        ("Sign in with issuer.test", "/auth/login/default?returnUrl=%2Faccount", true),
        ("Sign in with Google", "/auth/login/google?returnUrl=%2Faccount", true),
        ("Sign in with Work account", "/auth/login/work?returnUrl=%2Faccount", true),
    ];

    [Fact]
    public async Task An_address_nobody_invited_is_refused_with_every_way_in()
    {
        var page = await RefusedPageAsync(Sue);

        var (heading, text, buttons) = Read(page);
        Assert.Equal("Pollyanna doesn't know you yet", heading);
        Assert.Equal("Pollyanna doesn't know Sue@Example.com yet. Ask whoever organizes your family's exchange to invite that address, or sign in another way.", text);
        Assert.Equal("Sue@Example.com", page.QuerySelector(".empty p strong")!.TextContent);
        Assert.Equal(EveryWayIn, buttons);
        // The provider's icon shows beside its label, as on the chooser.
        var icons = page.QuerySelectorAll(".actions .button img").Select(i => i.GetAttribute("src")).ToList();
        Assert.StartsWith("/auth/icon/google?v=", Assert.Single(icons));
        await AssertNothingStoredAsync(0, 0, 0);
        AssertRefusalLogged("no invitation or person");
    }

    [Fact]
    public async Task An_invited_verified_address_gets_in_and_joins_every_inviting_event()
    {
        var open = await InviteAsync("sue@example.com");
        var draft = await InviteAsync("SUE@example.com", EventStatus.Draft);

        var (browser, location) = await SignInThroughGoogleAsync(Sue);
        using var _ = browser;

        Assert.Equal("/account", location);
        var sue = await app.PersonIdAsync(TestApp.IssuerOf("google"), Sue.Sub);
        Assert.Contains(sue, await app.ParticipantsAsync(open));
        Assert.Contains(sue, await app.ParticipantsAsync(draft));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
        // A full person: they can create an event of their own.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/events/new")).StatusCode);
    }

    [Fact]
    public async Task A_verified_email_matching_one_person_adds_a_login_to_them()
    {
        var sue = await app.PersonAsync("Sue", "sue@example.com");

        var (browser, location) = await SignInThroughGoogleAsync(Sue);
        using var _ = browser;

        Assert.Equal("/account", location);
        Assert.Equal(sue, await app.PersonIdAsync(TestApp.IssuerOf("google"), Sue.Sub));
        Assert.Equal(2, await app.WithDbAsync(db => db.Logins.CountAsync(l => l.PersonId == sue)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_unverified_or_unconfirmed_address_is_refused_even_when_invited(bool? verified)
    {
        await InviteAsync("sue@example.com");
        await app.PersonAsync("Sue", "sue@example.com", verified: true);
        var (people, logins) = (await PeopleAsync(), await LoginsAsync());

        var page = await RefusedPageAsync(Sue with { EmailVerified = verified });

        var (heading, text, buttons) = Read(page);
        Assert.Equal("Google hasn't confirmed this address", heading);
        Assert.Equal("Google says it hasn't verified Sue@Example.com, so Pollyanna can't use it to let you in. Confirm it with Google, or sign in another way.", text);
        Assert.Equal(EveryWayIn, buttons);
        await AssertNothingStoredAsync(people, logins, 0);
        Assert.Equal(1, await app.WithDbAsync(db => db.Invitations.CountAsync()));
        AssertRefusalLogged("email not verified");
    }

    [Fact]
    public async Task An_address_two_people_use_is_refused_even_with_an_invitation_pending()
    {
        await app.PersonAsync("Sue", "sue@example.com");
        await app.PersonAsync("Sue again", "SUE@example.com");
        await InviteAsync("sue@example.com");
        var (people, logins) = (await PeopleAsync(), await LoginsAsync());

        var page = await RefusedPageAsync(Sue);

        var (heading, text, buttons) = Read(page);
        Assert.Equal("Pollyanna can't tell who you are", heading);
        Assert.Equal("More than one person in Pollyanna uses Sue@Example.com. Ask whoever organizes your family's exchange to sort it out, or sign in another way.", text);
        Assert.Equal(EveryWayIn, buttons);
        await AssertNothingStoredAsync(people, logins, 0);
        AssertRefusalLogged("ambiguous: 2 matches");
    }

    [Fact]
    public async Task A_deleted_accounts_email_never_lets_anyone_in()
    {
        var (browser, _) = await app.SignInAsync(Sue with { Sub = "sue-family" });
        using (browser)
            Assert.Equal(HttpStatusCode.Redirect, (await browser.PostWithTokenAsync("/account/delete", "/account/delete")).StatusCode);
        var people = await PeopleAsync();

        var page = await RefusedPageAsync(Sue);

        Assert.Equal("Pollyanna doesn't know you yet", Read(page).Heading);
        await AssertNothingStoredAsync(people, 0, 0);
    }

    [Fact]
    public async Task A_canceled_invitation_no_longer_lets_anyone_in()
    {
        var (organizer, org) = await app.SignInAsync(new("org", "Org", "org@example.com", true, null));
        using var _o = organizer;
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [org]);
        await organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/addpeople", Seed.Form(("email", "sue@example.com")));
        Assert.Equal(1, await app.WithDbAsync(db => db.Invitations.CountAsync()));
        await organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/cancelinvite");

        var page = await RefusedPageAsync(Sue);

        Assert.Equal("Pollyanna doesn't know you yet", Read(page).Heading);
        Assert.Equal(1, await PeopleAsync());
    }

    [Fact]
    public async Task A_login_linked_earlier_keeps_working_after_its_invitations_and_events_are_gone()
    {
        var ev = await InviteAsync("sue@example.com");
        var (first, sue) = await app.SignInAsync(Sue, provider: "google");
        first.Dispose();
        await app.WithDbAsync(async db =>
        {
            db.Participants.RemoveRange(db.Participants);
            db.Organizers.RemoveRange(db.Organizers);
            db.Events.RemoveRange(db.Events.Where(e => e.Id == ev));
            await db.SaveChangesAsync();
        });

        var (browser, location) = await SignInThroughGoogleAsync(Sue);
        using var _ = browser;

        Assert.Equal("/account", location);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account")).StatusCode);
        Assert.Equal(sue, await app.PersonIdAsync(TestApp.IssuerOf("google"), Sue.Sub));
    }

    [Fact]
    public async Task A_known_login_without_email_verified_accepts_no_invitation_through_an_open_provider()
    {
        var sue = await app.PersonAsync("Sue", "sue@example.com", logins: [(TestApp.IssuerOf("google"), Sue.Sub)]);
        var ev = await InviteAsync("sue@example.com");

        var (browser, location) = await SignInThroughGoogleAsync(Sue with { EmailVerified = null });
        using var _ = browser;

        Assert.Equal("/account", location);
        Assert.DoesNotContain(sue, await app.ParticipantsAsync(ev));
        Assert.Equal(1, await app.WithDbAsync(db => db.Invitations.CountAsync()));
    }

    [Fact]
    public async Task A_known_login_without_email_verified_still_accepts_invitations_through_a_trusted_provider()
    {
        var sue = await app.PersonAsync("Sue", "sue@example.com", logins: [(TestApp.IssuerOf("work"), "sue-work")]);
        var ev = await InviteAsync("sue@example.com");

        var (browser, _) = await app.SignInAsync(Sue with { Sub = "sue-work", EmailVerified = null }, provider: "work");
        using var _b = browser;

        Assert.Contains(sue, await app.ParticipantsAsync(ev));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
    }

    [Fact]
    public async Task The_missing_email_verified_hint_for_an_open_provider_says_new_sign_ins_are_refused()
    {
        await RefusedPageAsync(Sue with { EmailVerified = null });

        var hint = Assert.Single(app.Logs.Entries, e => e.Message.Contains("sent no email_verified claim")).Message;
        Assert.Contains("provider google", hint);
        Assert.Contains("new sign-ins through it are refused", hint);
        Assert.Contains("a missing claim counts as unverified", hint);
        Assert.DoesNotContain("Invitations by email still match", hint);
        Assert.DoesNotContain("sue", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_trusted_provider_still_creates_a_new_person_for_an_unknown_login()
    {
        var (browser, _) = await app.SignInAsync(Sue with { Sub = "sue-work" }, provider: "work");
        using var _ = browser;

        Assert.Equal(1, await PeopleAsync());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account")).StatusCode);
    }

    [Theory]
    [InlineData("/auth/failed?refused=not-a-real-token")]
    [InlineData("/auth/failed?reason=refused&address=evil%40example.com&refused=evil%40example.com")]
    public async Task A_crafted_failed_url_shows_no_address(string url)
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync(url);

        Assert.Equal("Sign-in didn't work", Read(page).Heading);
        Assert.DoesNotContain("evil", page.Body!.TextContent);
        Assert.Null(page.QuerySelector(".empty strong"));
    }

    [Fact]
    public async Task A_refusal_link_from_another_page_cant_be_altered()
    {
        var (browser, location) = await SignInThroughGoogleAsync(Sue);
        using var _ = browser;
        var token = location[(location.IndexOf('=') + 1)..];

        var tampered = await browser.GetPageAsync("/auth/failed?refused=" + token[..^4] + (token.EndsWith("AAAA") ? "BBBB" : "AAAA"));

        Assert.Equal("Sign-in didn't work", Read(tampered).Heading);
        Assert.DoesNotContain("Sue@Example.com", tampered.Body!.TextContent);
    }
}
