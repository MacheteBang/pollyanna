using System.Net;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>
/// Several trusted providers side by side: each has its own callback, the chooser lists them, and
/// the same verified email through a second provider adds a second login (spec §2, §3, §7).
/// </summary>
public sealed class SeveralProvidersTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() =>
        app = await TestApp.StartAsync(providers: [new("google", "Google"), new("work", "Work account")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    static readonly MockIdentity Alice = new("alice-1", "Alice Example", "alice@example.com", true, null);

    Task<List<(string Issuer, string Sub)>> LoginsAsync(Guid person) =>
        app.WithDbAsync(async db => (await db.Logins.Where(l => l.PersonId == person).OrderBy(l => l.Issuer)
            .Select(l => new { l.Issuer, l.Subject }).ToListAsync()).Select(l => (l.Issuer, l.Subject)).ToList());

    [Fact]
    public async Task The_chooser_lists_every_provider_in_order_keeps_the_return_url_and_sets_no_cookie()
    {
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync("/auth/login?returnUrl=" + Uri.EscapeDataString("/account?tab=x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Nothing is stored: at most the notice cookie is cleared, as on every page.
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var set) ? set.ToList() : [];
        Assert.All(cookies, c => Assert.Matches("^[^=]+=;", c));
        var page = await TestBrowser.ParseAsync(response);
        Assert.Equal("Sign in to Pollyanna", page.QuerySelector("h1")!.TextContent);
        Assert.Contains("Pollyanna is your family's gift exchange", page.Body!.TextContent);
        var buttons = page.QuerySelectorAll(".actions a.button").Select(a => (a.TextContent.Trim(), a.GetAttribute("href"))).ToList();
        Assert.Equal(
        [
            ("Continue with issuer.test", "/auth/login/default?returnUrl=%2Faccount%3Ftab%3Dx"),
            ("Continue with Google", "/auth/login/google?returnUrl=%2Faccount%3Ftab%3Dx"),
            ("Continue with Work account", "/auth/login/work?returnUrl=%2Faccount%3Ftab%3Dx"),
        ], buttons);
    }

    [Fact]
    public async Task A_listed_provider_uses_its_own_client_and_callback()
    {
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync("/auth/login/google");

        var location = response.Headers.Location!;
        Assert.Equal(TestApp.IssuerOf("google") + "/authorize", location.GetLeftPart(UriPartial.Path));
        var q = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(TestApp.ClientIdOf("google"), q["client_id"]);
        Assert.Equal(TestApp.PublicUrl + "/auth/callback/google", q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(["email", "openid", "profile"], q["scope"].ToString().Split(' ').Order());
    }

    [Fact]
    public async Task A_person_signs_in_through_a_listed_provider_and_returns_to_the_page_asked_for()
    {
        using var browser = app.NewBrowser();

        var callback = await browser.SignInAsync(Alice, "/account", provider: "google");

        Assert.Equal("/account", callback.Headers.Location!.OriginalString);
        var person = await app.PersonIdAsync(TestApp.IssuerOf("google"), Alice.Sub);
        Assert.Equal([(TestApp.IssuerOf("google"), Alice.Sub)], await LoginsAsync(person));
        Assert.Contains("Alice Example", (await browser.GetPageAsync("/account")).Body!.TextContent);
        Assert.Contains(app.Logs.Entries, e => e.Category == "Pollyanna.Access" && e.Message == "GET /auth/callback/google 302");
    }

    [Fact]
    public async Task The_same_verified_email_through_a_second_provider_adds_a_second_login()
    {
        var (_, alice) = await app.SignInAsync(Alice);

        var (browser, again) = await app.SignInAsync(Alice with { Sub = "alice-google" }, provider: "google");

        Assert.Equal(alice, again);
        Assert.Equal([(TestApp.IssuerOf("google"), "alice-google"), (TestApp.Issuer, Alice.Sub)], await LoginsAsync(alice));
        Assert.Equal(1, await app.WithDbAsync(db => db.People.CountAsync()));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account")).StatusCode);
    }

    [Fact]
    public async Task An_unverified_email_through_a_second_provider_makes_a_new_person()
    {
        var (_, alice) = await app.SignInAsync(Alice);

        var (_, other) = await app.SignInAsync(Alice with { Sub = "alice-work", EmailVerified = null }, provider: "work");

        Assert.NotEqual(alice, other);
    }

    [Fact]
    public async Task The_missing_email_verified_hint_is_logged_once_per_provider()
    {
        var unverified = new MockIdentity("u", "U", "u@example.com", null, null);
        await app.SignInAsync(unverified);
        await app.SignInAsync(unverified with { Sub = "u2" });
        await app.SignInAsync(unverified with { Sub = "u3" }, provider: "google");

        var hints = app.Logs.Entries.Where(e => e.Message.Contains("sent no email_verified claim")).Select(e => e.Message).ToList();
        Assert.Equal(2, hints.Count);
        Assert.Contains(hints, m => m.Contains("provider default"));
        Assert.Contains(hints, m => m.Contains("provider google"));
        Assert.DoesNotContain(hints, m => m.Contains("u@example.com"));
    }

    [Fact]
    public async Task An_unknown_provider_key_is_not_found()
    {
        using var browser = app.NewBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/auth/login/nobody")).StatusCode);
    }
}
