using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>Home (with its empty state), the about line and account pages (spec §7).</summary>
public sealed class PageTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    async Task<(TestBrowser Browser, Guid PersonId)> SignInAsync(MockIdentity identity)
    {
        var browser = app.NewBrowser();
        await browser.SignInAsync(identity);
        var id = await app.WithDbAsync(db => db.People.Where(p => p.OidcSub == identity.Sub).Select(p => p.Id).SingleAsync());
        return (browser, id);
    }

    async Task<Guid> SeedPersonAsync(string name)
    {
        var p = new Person { OidcIssuer = "https://other.test", OidcSub = name, DisplayName = name, CreatedAt = DateTime.UtcNow };
        await app.WithDbAsync(async db => { db.People.Add(p); await db.SaveChangesAsync(); });
        return p.Id;
    }

    async Task SeedEventAsync(string name, EventStatus status, Guid creator, Guid[] organizers, Guid[] participants, string budget = "$25")
    {
        var ev = new Event { Name = name, BudgetText = budget, ExchangeDate = new DateOnly(2026, 12, 24), Status = status, CreatedBy = creator, CreatedAt = DateTime.UtcNow };
        ev.Organizers.AddRange(organizers.Select(o => new Organizer { PersonId = o }));
        ev.Participants.AddRange(participants.Select(p => new Participant { PersonId = p, JoinedAt = DateTime.UtcNow }));
        await app.WithDbAsync(async db => { db.Events.Add(ev); await db.SaveChangesAsync(); });
    }

    [Fact]
    public async Task First_sign_in_shows_the_empty_home_with_the_email_to_be_invited_at()
    {
        var (browser, _) = await SignInAsync(new("sam", "Sam", "sam.r@example.com", true, null));
        using var _b = browser;
        var page = await browser.GetPageAsync("/");
        var text = page.Body!.TextContent;

        Assert.Contains("Nothing to unwrap yet", text);
        Assert.Contains("Ask the organizer to invite this email", text);
        Assert.Equal("sam.r@example.com", page.QuerySelector(".email-box")!.TextContent);
        Assert.NotNull(page.QuerySelector("a[href='/events/new']"));
        Assert.Null(page.QuerySelector("a[href='/past']"));
        Assert.Equal("Sam", page.QuerySelector(".whoami a[href='/account']")!.TextContent);
    }

    [Fact]
    public async Task Empty_state_explains_when_there_is_no_email()
    {
        var (browser, _) = await SignInAsync(new("noemail", "No Email", null, null, null));
        using var _b = browser;
        var page = await browser.GetPageAsync("/");
        Assert.Contains("has no email address", page.Body!.TextContent);
        Assert.Null(page.QuerySelector(".email-box"));
    }

    [Fact]
    public async Task Empty_state_explains_when_the_email_is_unverified()
    {
        var (browser, _) = await SignInAsync(new("unv", "Unverified", "unv@example.com", false, null));
        using var _b = browser;
        var page = await browser.GetPageAsync("/");
        Assert.Contains("isn't verified", page.Body!.TextContent);
    }

    [Fact]
    public async Task Home_lists_only_my_events_hiding_drafts_I_dont_organize_and_archived_ones()
    {
        var (browser, me) = await SignInAsync(new("me", "Me", "me@example.com", true, null));
        using var _b = browser;
        var other = await SeedPersonAsync("Other");

        await SeedEventAsync("Open I'm in", EventStatus.Open, other, [other], [me, other]);
        await SeedEventAsync("Draft I'm in", EventStatus.Draft, other, [other], [me, other]);
        await SeedEventAsync("Draft I organize", EventStatus.Draft, me, [me], [other]);
        await SeedEventAsync("Drawn I organize and am in", EventStatus.Drawn, me, [me], [me, other]);
        await SeedEventAsync("Not mine", EventStatus.Open, other, [other], [other]);
        await SeedEventAsync("Archived I was in", EventStatus.Archived, other, [other], [me]);
        await SeedEventAsync("Archived I organized", EventStatus.Archived, me, [me], []);

        var page = await browser.GetPageAsync("/");
        var titles = page.QuerySelectorAll(".slip-title").Select(e => e.TextContent).Order().ToArray();

        Assert.Equal(["Draft I organize", "Drawn I organize and am in", "Open I'm in"], titles);
        Assert.Equal("Past events (2)", page.QuerySelector("a[href='/past']")!.TextContent.Trim());
        Assert.DoesNotContain("Nothing to unwrap yet", page.Body!.TextContent);
    }

    [Fact]
    public async Task Event_slip_shows_stamp_budget_date_and_organizer_line()
    {
        var (browser, me) = await SignInAsync(new("me", "Me", "me@example.com", true, null));
        using var _b = browser;
        var other = await SeedPersonAsync("Other");
        await SeedEventAsync("Cousins' Swap", EventStatus.Draft, me, [me], [me, other], budget: "$15");

        var page = await browser.GetPageAsync("/");
        var slip = page.QuerySelector("a.slip")!;
        Assert.StartsWith("/events/", slip.GetAttribute("href"));
        Assert.Equal("DRAFT", slip.QuerySelector(".stamp")!.TextContent);
        Assert.Contains("stamp-ink", slip.QuerySelector(".stamp")!.ClassList);
        Assert.Contains("$15 · Dec 24, 2026", slip.TextContent);
        Assert.Contains("You organize · 2 people", slip.TextContent);
    }

    const string AboutLine = "Every family has a version of this: names on folded slips, a hat going round the table, someone squinting at the handwriting. Around Philadelphia they call it a Pollyanna. This is the hat, minus the squinting.";

    [Fact]
    public async Task Home_says_what_Pollyanna_is_with_and_without_events()
    {
        var (browser, me) = await SignInAsync(new("me", "Me", "me@example.com", true, null));
        using var _b = browser;

        var empty = await browser.GetPageAsync("/");
        Assert.Equal(AboutLine, empty.QuerySelector(".about")!.TextContent.Trim());

        var other = await SeedPersonAsync("Other");
        await SeedEventAsync("Open I'm in", EventStatus.Open, other, [other], [me, other]);
        var listed = await browser.GetPageAsync("/");
        Assert.NotNull(listed.QuerySelector(".slip-title"));
        Assert.Equal(AboutLine, listed.QuerySelector(".about")!.TextContent.Trim());
    }

    [Fact]
    public async Task Signed_out_page_says_what_Pollyanna_is()
    {
        using var browser = app.NewBrowser();
        var page = await browser.GetPageAsync("/auth/signed-out");
        Assert.Equal(AboutLine, page.QuerySelector(".about")!.TextContent.Trim());
    }

    [Fact]
    public async Task Account_page_shows_name_and_email_as_sent_and_a_sign_out_form()
    {
        var (browser, _) = await SignInAsync(new("pat", "Pat Example", "pat@example.com", false, null));
        using var _b = browser;
        var page = await browser.GetPageAsync("/account");
        var text = page.Body!.TextContent;

        Assert.Contains("Pat Example", text);
        Assert.Contains("pat@example.com", text);
        Assert.Contains("(not verified)", text);
        var form = page.QuerySelector("main form[action='/auth/signout']")!;
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.NotNull(form.QuerySelector("input[name='__RequestVerificationToken']"));
    }

    [Fact]
    public async Task Pages_escape_names_from_the_provider()
    {
        var (browser, _) = await SignInAsync(new("x", "<script>alert(1)</script>", "x@example.com", true, null));
        using var _b = browser;
        var html = await (await browser.GetAsync("/account")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Unknown_pages_show_the_not_found_page()
    {
        var (browser, _) = await SignInAsync(new("me", "Me", null, null, null));
        using var _b = browser;
        var response = await browser.GetAsync("/no/such/page");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Not here", await response.Content.ReadAsStringAsync());
    }
}
