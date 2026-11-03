using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>The event page for participants (spec §4, §7): the people in the hat, notes, leaving.</summary>
public sealed class ParticipantTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("sam", "Sam", "sam@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    Task<Participant> MineAsync(Guid ev) => app.WithDbAsync(db => db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == me));

    [Fact]
    public async Task Event_page_shows_details_and_everyone_elses_notes_but_no_organizer_only_facts()
    {
        var org = await app.PersonAsync("Org");
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [org], [me, carol], budget: "$25");
        await app.WithDbAsync(async db =>
        {
            var c = await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == carol);
            c.Notes = "Socks: women's M.\nNo candles please.";
            c.DoneShopping = true;
            db.Invitations.Add(new Invitation { EventId = ev, Email = "secret@example.com", EmailNormalized = "secret@example.com", InvitedBy = org });
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync($"/events/{ev}");
        var text = page.Body!.TextContent;
        Assert.Equal("Stockings 2026", page.QuerySelector("h1")!.TextContent);
        Assert.Contains("$25 · Dec 24, 2026", text);
        Assert.NotNull(page.QuerySelector(".page-head [data-countdown='2026-12-24']"));
        Assert.Contains("In the hat (2)", text);
        Assert.Contains("Socks: women's M.\nNo candles please.", page.QuerySelector(".people .notes")!.TextContent);
        // Organizer-only things stay off the participant's page.
        Assert.DoesNotContain("secret@example.com", text);
        Assert.DoesNotContain("shopping", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Org", page.QuerySelector("main")!.TextContent);
    }

    [Fact]
    public async Task Saving_notes_bumps_the_version_only_when_they_change()
    {
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me]);

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", "Size L everything.\r\nCoffee.")));
        var mine = await MineAsync(ev);
        Assert.Equal(("Size L everything.\nCoffee.", 1), (mine.Notes, mine.NotesVersion));

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", "Size L everything.\nCoffee.  ")));
        Assert.Equal(1, (await MineAsync(ev)).NotesVersion);

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", "Tea now.")));
        Assert.Equal(2, (await MineAsync(ev)).NotesVersion);

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Equal("Tea now.", page.QuerySelector("textarea[name='notes']")!.TextContent);
        Assert.Equal("Notes saved.", page.QuerySelector(".notice")!.TextContent);
    }

    [Fact]
    public async Task Notes_have_a_length_limit()
    {
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me]);
        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", new string('x', 4001))));
        Assert.Equal("", (await MineAsync(ev)).Notes);
        Assert.Contains("under 4,000", (await browser.GetPageAsync($"/events/{ev}")).QuerySelector(".notice-error")!.TextContent);
    }

    [Fact]
    public async Task Notes_are_read_only_once_archived()
    {
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Archived, [org], [me]);
        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Null(page.QuerySelector("textarea"));
        await browser.PostWithTokenAsync("/account", $"/events/{ev}/notes", Form(("notes", "late")));
        Assert.Equal("", (await MineAsync(ev)).Notes);
    }

    [Fact]
    public async Task Only_the_participant_edits_their_own_notes()
    {
        // An organizer who isn't taking part has no notes form, and the notes action ignores them.
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [carol]);
        await browser.PostWithTokenAsync("/account", $"/events/{ev}/notes", Form(("notes", "overwritten")));
        Assert.Equal("", await app.WithDbAsync(db => db.Participants.Where(p => p.PersonId == carol).Select(p => p.Notes).SingleAsync()));
    }

    [Fact]
    public async Task Leaving_takes_a_confirmation_and_removes_me()
    {
        var org = await app.PersonAsync("Org");
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me, carol]);
        await app.WithDbAsync(async db => { db.Exclusions.Add(Exclusion.Between(ev, me, carol)); await db.SaveChangesAsync(); });

        var eventPage = await browser.GetPageAsync($"/events/{ev}");
        Assert.NotNull(eventPage.QuerySelector($"a[href='/events/{ev}/leave']"));
        var confirm = await browser.GetPageAsync($"/events/{ev}/leave");
        Assert.Contains("Leave Stockings?", confirm.QuerySelector("h1")!.TextContent);
        Assert.Contains(me, await app.ParticipantsAsync(ev)); // nothing happens until confirmed

        var response = await browser.SubmitAsync(confirm, $"/events/{ev}/leave");
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain(me, await app.ParticipantsAsync(ev));
        Assert.Equal(0, await app.WithDbAsync(db => db.Exclusions.CountAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/events/{ev}")).StatusCode);
        Assert.Contains("You've left Stockings.", (await browser.GetPageAsync("/")).QuerySelector(".notice")!.TextContent);
    }

    [Fact]
    public async Task Leaving_keeps_an_organizer_role()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        var response = await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave");

        Assert.Equal($"/events/{ev}/manage", response.Headers.Location!.OriginalString);
        Assert.Empty(await app.ParticipantsAsync(ev));
        Assert.Equal([me], await app.OrganizersAsync(ev));
    }
}
