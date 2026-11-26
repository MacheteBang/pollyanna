using System.Net;
using AngleSharp.Html.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>"Ways to sign in" on <c>/account</c>: list, add and remove logins (spec §3, §7).</summary>
public sealed class AccountLoginsTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() =>
        app = await TestApp.StartAsync(providers: [new("google", "Google"), new("work", "Work account")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    static readonly MockIdentity Alice = new("alice-1", "Alice Example", "alice@example.com", true, null);

    Task<List<(string Issuer, string Sub)>> LoginsAsync(Guid person) =>
        app.WithDbAsync(async db => (await db.Logins.Where(l => l.PersonId == person).OrderBy(l => l.Issuer).ThenBy(l => l.Subject)
            .Select(l => new { l.Issuer, l.Subject }).ToListAsync()).Select(l => (l.Issuer, l.Subject)).ToList());

    static List<string> Rows(IHtmlDocument page) =>
        [.. page.QuerySelectorAll("#logins li.person .person-body").Select(r => string.Join(" ", r.TextContent.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries)))];

    static List<string> AddButtons(IHtmlDocument page) =>
        [.. page.QuerySelectorAll("#logins form[action='/account/addlogin'] button").Select(b => b.TextContent.Trim())];

    /// <summary>Presses "Add <paramref name="key"/>" and completes the round trip at that provider as <paramref name="identity"/>.</summary>
    static async Task<HttpResponseMessage> AddLoginAsync(TestBrowser browser, string key, MockIdentity identity)
    {
        var page = await browser.GetPageAsync("/account");
        var toIssuer = await browser.SubmitAsync(page, "/account/addlogin", match: f => f.QuerySelector($"input[name='key'][value='{key}']") is not null);
        return await browser.CompleteAtIssuerAsync(toIssuer, identity);
    }

    static async Task<string> NoticeAfterAsync(TestBrowser browser, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location!.OriginalString);
        var page = await browser.GetPageAsync("/account");
        return page.QuerySelector(".notice")!.TextContent.Trim();
    }

    async Task<Guid> LoginIdAsync(string issuer, string sub) =>
        await app.WithDbAsync(db => db.Logins.Where(l => l.Issuer == issuer && l.Subject == sub).Select(l => l.Id).SingleAsync());

    Task<HttpResponseMessage> RemoveAsync(TestBrowser browser, Guid loginId) =>
        browser.SubmitOnAsync("/account", "/account/removelogin", match: f => f.QuerySelector($"input[name='loginId'][value='{loginId}']") is not null);

    [Fact]
    public async Task The_list_shows_each_logins_label_and_last_email_and_marks_a_removed_provider()
    {
        var (browser, alice) = await app.SignInAsync(Alice);
        await app.WithDbAsync(async db =>
        {
            db.Logins.Add(new Login { PersonId = alice, Issuer = TestApp.IssuerOf("google"), Subject = "g", LastEmail = "alice@gmail.example", CreatedAt = DateTime.UtcNow.AddMinutes(1) });
            db.Logins.Add(new Login { PersonId = alice, Issuer = "https://gone.example", Subject = "x", LastEmail = "old@example.com", CreatedAt = DateTime.UtcNow.AddMinutes(2) });
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync("/account");

        Assert.Equal("Ways to sign in", page.QuerySelector("#logins h2")!.TextContent);
        Assert.Null(page.QuerySelector("#profile-heading"));
        Assert.Equal(
            ["issuer.test alice@example.com", "Google alice@gmail.example", "gone.example (no longer set up) old@example.com"],
            Rows(page));
        Assert.Equal(3, page.QuerySelectorAll("#logins form[action='/account/removelogin'] button").Length);
        Assert.Contains("added back", page.QuerySelector("#logins")!.TextContent);
    }

    [Fact]
    public async Task With_several_providers_there_is_an_add_button_per_provider_in_order()
    {
        var (browser, _) = await app.SignInAsync(Alice);

        var page = await browser.GetPageAsync("/account");

        Assert.Equal(["Add issuer.test", "Add Google", "Add Work account"], AddButtons(page));
        Assert.All(page.QuerySelectorAll("#logins form[action='/account/addlogin'] button"),
            b => Assert.Contains("button-outline", b.ClassList));
    }

    [Fact]
    public async Task With_one_provider_the_list_and_remove_buttons_show_without_add_buttons()
    {
        await using var single = await TestApp.StartAsync();
        var (browser, _) = await single.SignInAsync(Alice);

        var page = await browser.GetPageAsync("/account");

        Assert.Equal(["issuer.test alice@example.com"], Rows(page));
        Assert.Single(page.QuerySelectorAll("#logins form[action='/account/removelogin']"));
        Assert.Empty(AddButtons(page));
        Assert.Equal(HttpStatusCode.NotFound,
            (await browser.PostWithTokenAsync("/account", "/account/addlogin", Seed.Form(("key", "default")))).StatusCode);
    }

    [Fact]
    public async Task Adding_a_login_with_a_different_email_attaches_it_and_updates_the_profile()
    {
        var (browser, alice) = await app.SignInAsync(Alice);
        app.Pictures.Serve("https://pics.example/a.png", TestImages.Png, "image/png");
        var other = new MockIdentity("alice-g", "Alice G", "alice.g@gmail.example", false, "https://pics.example/a.png");

        var notice = await NoticeAfterAsync(browser, await AddLoginAsync(browser, "google", other));

        Assert.Equal("Added.", notice);
        Assert.Equal([(TestApp.IssuerOf("google"), "alice-g"), (TestApp.Issuer, Alice.Sub)], await LoginsAsync(alice));
        var person = await app.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.Id == alice));
        Assert.Equal(("Alice G", "alice.g@gmail.example"), (person.DisplayName, person.Email));
        Assert.Equal("https://pics.example/a.png", person.PictureUrl);
        Assert.True(await app.WithDbAsync(db => db.PersonPictures.AnyAsync(p => p.PersonId == alice)));
        Assert.Equal(1, await app.WithDbAsync(db => db.People.CountAsync()));
        Assert.Equal(1, await app.WithDbAsync(db => db.Sessions.CountAsync()));
        Assert.Contains("Google alice.g@gmail.example", Rows(await browser.GetPageAsync("/account")));
        Assert.Contains(app.Logs.Entries, e => e.Message == $"Person {alice} added a login through provider google.");
    }

    [Fact]
    public async Task A_second_login_from_the_same_provider_is_allowed()
    {
        var (browser, alice) = await app.SignInAsync(Alice);

        await AddLoginAsync(browser, "default", Alice with { Sub = "alice-2", Email = "alice2@example.com" });

        Assert.Equal([(TestApp.Issuer, Alice.Sub), (TestApp.Issuer, "alice-2")], await LoginsAsync(alice));
    }

    [Fact]
    public async Task Adding_a_login_that_is_already_yours_changes_nothing()
    {
        var (browser, alice) = await app.SignInAsync(Alice);

        var notice = await NoticeAfterAsync(browser, await AddLoginAsync(browser, "default", Alice with { Name = "Changed" }));

        Assert.Equal("That's already one of your ways to sign in.", notice);
        Assert.Equal([(TestApp.Issuer, Alice.Sub)], await LoginsAsync(alice));
        Assert.Equal("Alice Example", await app.WithDbAsync(db => db.People.Where(p => p.Id == alice).Select(p => p.DisplayName).SingleAsync()));
    }

    [Fact]
    public async Task Adding_another_persons_login_is_refused_and_nothing_is_merged()
    {
        var bob = await app.PersonAsync("Bob", "bob@example.com", logins: [(TestApp.IssuerOf("google"), "bob-g")]);
        var (browser, alice) = await app.SignInAsync(Alice);

        var notice = await NoticeAfterAsync(browser, await AddLoginAsync(browser, "google", new("bob-g", "Bob", "bob@example.com", true, null)));

        Assert.Equal("That sign-in already belongs to another Pollyanna account.", notice);
        Assert.Equal([(TestApp.Issuer, Alice.Sub)], await LoginsAsync(alice));
        Assert.Equal([(TestApp.IssuerOf("google"), "bob-g")], await LoginsAsync(bob));
        Assert.Equal("Alice Example", await app.WithDbAsync(db => db.People.Where(p => p.Id == alice).Select(p => p.DisplayName).SingleAsync()));
        Assert.Contains(app.Logs.Entries, e => e.Message == $"Person {alice} tried to add a login through provider google that belongs to another person.");
    }

    [Fact]
    public async Task An_add_finished_after_signing_in_as_someone_else_changes_nothing()
    {
        var (browser, alice) = await app.SignInAsync(Alice);
        var page = await browser.GetPageAsync("/account");
        var toIssuer = await browser.SubmitAsync(page, "/account/addlogin", match: f => f.QuerySelector("input[name='key'][value='google']") is not null);
        var callback = await browser.AuthorizeAtIssuerAsync(toIssuer, new("carol-g", "Carol", "carol@example.com", true, null));
        // Meanwhile the browser signs out and Bob signs in.
        await browser.SubmitOnAsync("/account", "/auth/signout");
        await browser.SignInAsync(new("bob-1", "Bob", "bob@example.com", true, null));
        var bob = await app.PersonIdAsync(TestApp.Issuer, "bob-1");

        var response = await browser.GetAsync(callback.PathAndQuery);

        Assert.StartsWith("/auth/failed", response.Headers.Location!.OriginalString);
        Assert.Equal([(TestApp.Issuer, Alice.Sub)], await LoginsAsync(alice));
        Assert.Equal([(TestApp.Issuer, "bob-1")], await LoginsAsync(bob));
        Assert.False(await app.WithDbAsync(db => db.Logins.AnyAsync(l => l.Subject == "carol-g")));
    }

    [Fact]
    public async Task Removing_the_login_in_use_keeps_the_session()
    {
        var (browser, alice) = await app.SignInAsync(Alice);
        await AddLoginAsync(browser, "google", Alice with { Sub = "alice-g" });

        var response = await RemoveAsync(browser, await LoginIdAsync(TestApp.Issuer, Alice.Sub));

        Assert.Equal("Removed.", await NoticeAfterAsync(browser, response));
        Assert.Equal([(TestApp.IssuerOf("google"), "alice-g")], await LoginsAsync(alice));
        Assert.Equal(["Google alice@example.com"], Rows(await browser.GetPageAsync("/account")));
        Assert.Contains(app.Logs.Entries, e => e.Message == $"Person {alice} removed a login through provider default.");
    }

    [Fact]
    public async Task A_login_from_a_removed_provider_can_be_removed()
    {
        var (browser, alice) = await app.SignInAsync(Alice);
        var gone = new Login { PersonId = alice, Issuer = "https://gone.example", Subject = "x", CreatedAt = DateTime.UtcNow };
        await app.WithDbAsync(async db => { db.Logins.Add(gone); await db.SaveChangesAsync(); });

        await RemoveAsync(browser, gone.Id);

        Assert.Equal([(TestApp.Issuer, Alice.Sub)], await LoginsAsync(alice));
        Assert.Contains(app.Logs.Entries, e => e.Message == $"Person {alice} removed a login through a provider that is no longer set up.");
    }

    [Fact]
    public async Task Removing_the_last_login_is_refused_with_a_pointer_to_account_deletion()
    {
        var (browser, alice) = await app.SignInAsync(Alice);

        var response = await RemoveAsync(browser, await LoginIdAsync(TestApp.Issuer, Alice.Sub));

        Assert.Equal("/account", response.Headers.Location!.OriginalString);
        var page = await browser.GetPageAsync("/account");
        var error = page.QuerySelector("#logins .notice-error")!;
        Assert.Contains("only way to sign in", error.TextContent);
        Assert.NotNull(error.QuerySelector("a[href='/account/delete']"));
        Assert.Equal([(TestApp.Issuer, Alice.Sub)], await LoginsAsync(alice));
    }

    [Fact]
    public async Task Someone_elses_login_cannot_be_removed()
    {
        var bob = await app.PersonAsync("Bob", "bob@example.com", logins: [("https://other.test", "b1"), ("https://other.test", "b2")]);
        var (browser, _) = await app.SignInAsync(Alice);
        var bobs = await LoginIdAsync("https://other.test", "b1");

        var response = await browser.PostWithTokenAsync("/account", "/account/removelogin", Seed.Form(("loginId", bobs.ToString())));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(2, (await LoginsAsync(bob)).Count);
    }

    [Fact]
    public async Task A_removed_login_whose_verified_email_matches_is_added_back_on_its_next_sign_in()
    {
        var (browser, alice) = await app.SignInAsync(Alice);
        await AddLoginAsync(browser, "google", Alice with { Sub = "alice-g" });
        await RemoveAsync(browser, await LoginIdAsync(TestApp.IssuerOf("google"), "alice-g"));

        var (_, again) = await app.SignInAsync(Alice with { Sub = "alice-g" }, provider: "google");

        Assert.Equal(alice, again);
        Assert.Equal(2, (await LoginsAsync(alice)).Count);
    }

    [Fact]
    public async Task Login_changes_log_no_email()
    {
        var bob = await app.PersonAsync("Bob", "bob@example.com", logins: [(TestApp.IssuerOf("work"), "bob-w")]);
        var (browser, _) = await app.SignInAsync(Alice);
        await AddLoginAsync(browser, "google", Alice with { Sub = "alice-g", Email = "alice.g@gmail.example" });
        await AddLoginAsync(browser, "work", new("bob-w", "Bob", "bob@example.com", true, null));
        await RemoveAsync(browser, await LoginIdAsync(TestApp.IssuerOf("google"), "alice-g"));

        Assert.DoesNotContain(app.Logs.Entries, e => e.Message.Contains('@'));
    }
}
