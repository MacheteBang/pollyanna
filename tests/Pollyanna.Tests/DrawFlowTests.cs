using System.Net;
using System.Security.Cryptography;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Drawing names from the manage page (spec §4, §5.1, §5.2), the reveal card and its markers
/// (§5.2, §7), "done shopping" and redraw.
/// </summary>
public sealed class DrawFlowTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    string Manage(Guid ev) => $"/events/{ev}/manage";

    Task<HttpResponseMessage> ActAsync(Guid ev, string handler, IEnumerable<KeyValuePair<string, string>>? fields = null) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/{handler}", fields);

    async Task<string?> NoticeAsync(string path)
    {
        var page = await browser.GetPageAsync(path);
        return (page.QuerySelector(".notice-error") ?? page.QuerySelector(".notice"))?.TextContent;
    }

    async Task<(Guid Event, Guid[] Others)> OpenEventAsync(int others, bool takePart = true)
    {
        var people = new List<Guid>();
        foreach (var name in new[] { "Ann", "Bob", "Carol", "Dave", "Erin", "Finn", "Gus" }.Take(others))
            people.Add(await app.PersonAsync(name));
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], takePart ? [me, .. people] : [.. people]);
        return (ev, [.. people]);
    }

    Task<int> AssignmentCountAsync(Guid ev) => app.WithDbAsync(db => db.Assignments.CountAsync(a => a.EventId == ev));

    [Fact]
    public async Task Drawing_names_seals_one_slip_per_person_under_the_giver_tag()
    {
        var (ev, others) = await OpenEventAsync(5);
        await app.HouseholdAsync(ev, "Mom & Dad", others[0], others[1]);

        var response = await ActAsync(ev, "draw");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        Assert.Contains("Names are drawn", await NoticeAsync(Manage(ev)));

        // Recompute every giver tag from the key file itself, exactly as spec §5.2 defines it.
        var keyBytes = await File.ReadAllBytesAsync(Path.Combine(app.DataDir, "secrets", "draw.key"));
        Guid[] everyone = [me, .. others];
        var tags = everyone.ToDictionary(p => Convert.ToHexString(HMACSHA256.HashData(keyBytes,
            (byte[])[.. "giver"u8.ToArray(), .. ev.ToByteArray(true), .. p.ToByteArray(true)])), p => p);
        var rows = await app.WithDbAsync(db => db.Assignments.AsNoTracking().Where(a => a.EventId == ev).ToListAsync());
        Assert.Equal(6, rows.Count);
        var pairs = rows.ToDictionary(r => tags[Convert.ToHexString(r.GiverTag)], r => r.RecipientId);
        Assert.Equal(everyone.Order(), pairs.Keys.Order());
        Assert.Equal(everyone.Order(), pairs.Values.Order());
        Assert.All(pairs, p => Assert.NotEqual(p.Key, p.Value));
        Assert.NotEqual(others[1], pairs[others[0]]);
        Assert.NotEqual(others[0], pairs[others[1]]);
        // Nothing names a giver: no row holds any participant's ID except as its recipient.
        Assert.All(rows, r => Assert.DoesNotContain(r.Id, everyone));
        Assert.All(rows, r => Assert.False(r.Viewed || r.RecipientChanged));

        // A second draw is refused.
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(6, await AssignmentCountAsync(ev));
    }

    [Fact]
    public async Task Draw_is_blocked_while_invitations_are_pending()
    {
        var (ev, _) = await OpenEventAsync(3);
        await app.WithDbAsync(async db =>
        {
            db.Invitations.Add(new Invitation { EventId = ev, Email = "sue@example.com", EmailNormalized = "sue@example.com", InvitedBy = me });
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Null(page.QuerySelector($"form[action='{Manage(ev)}/draw']"));
        Assert.NotNull(page.QuerySelector("#status button[disabled]"));
        Assert.Contains("1 invitation is still waiting", page.QuerySelector("#draw-blocked")!.TextContent);

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Equal(0, await AssignmentCountAsync(ev));
        Assert.Contains("invitation", await NoticeAsync(Manage(ev)));
    }

    [Fact]
    public async Task Draw_needs_three_people()
    {
        var (ev, _) = await OpenEventAsync(1);

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("at least 3 people", page.QuerySelector("#draw-blocked")!.TextContent);
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Equal(0, await AssignmentCountAsync(ev));
    }

    [Fact]
    public async Task Draw_cant_run_in_draft()
    {
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, ann, bob]);

        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/draw']"));
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(EventStatus.Draft, await app.StatusAsync(ev));
        Assert.Equal(0, await AssignmentCountAsync(ev));
    }

    [Fact]
    public async Task An_impossible_draw_names_the_rule_and_stores_nothing()
    {
        var (ev, others) = await OpenEventAsync(4);
        await app.HouseholdAsync(ev, "The Parks", others[0], others[1], others[2]);

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("The household The Parks has more than half", page.QuerySelector("#draw-blocked")!.TextContent);

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Equal(0, await AssignmentCountAsync(ev));
        Assert.Contains("more than half", await NoticeAsync(Manage(ev)));
    }

    [Theory]
    [InlineData(3, 0, true)]    // 4 people
    [InlineData(4, 0, false)]   // 5 people, 44 valid assignments
    [InlineData(4, 1, false)]   // 5 people, one couple: 24
    [InlineData(4, 2, true)]    // 5 people, two couples: 16
    public async Task The_predictability_warning_shows_at_its_thresholds(int others, int couples, bool warned)
    {
        var (ev, people) = await OpenEventAsync(others);
        for (var i = 0; i < couples; i++)
            await app.HouseholdAsync(ev, $"Couple {i}", people[2 * i], people[2 * i + 1]);

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Equal(warned, page.QuerySelector("#draw-warning") is not null);
        var button = page.QuerySelector($"form[action='{Manage(ev)}/draw'] button")!;
        Assert.Equal(warned ? "Draw anyway" : "Draw names", button.TextContent);

        // Drawing anyway works.
        await ActAsync(ev, "draw");
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task The_event_page_shows_my_recipient_behind_tap_to_reveal()
    {
        var (ev, others) = await OpenEventAsync(3);
        var (ann, bob, carol) = (others[0], others[1], others[2]);
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == carol)).Notes = "Lemon anything";
            await db.SaveChangesAsync();
        });
        await app.SealAsync(ev, Loop(me, carol, ann, bob));

        var page = await browser.GetPageAsync($"/events/{ev}");
        var card = page.QuerySelector("details.reveal[data-reveal]")!;
        Assert.False(card.HasAttribute("open"));
        Assert.Contains("Pull the ribbon", card.QuerySelector("summary")!.TextContent);
        Assert.Equal("Carol", card.QuerySelector(".reveal-name")!.TextContent);
        Assert.Contains("Lemon anything", card.TextContent);
        Assert.Null(card.QuerySelector(".stamp"));
        Assert.DoesNotContain("drawn again", card.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(page.QuerySelector($"form[action='/events/{ev}/shopping']"));

        // Opening the card is "viewed assignment", which organizers see as a mark.
        Assert.False((await app.AssignmentOfAsync(ev, me)).Viewed);
        var response = await browser.SubmitAsync(page, $"/events/{ev}/seen");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await app.AssignmentOfAsync(ev, me)).Viewed);
        Assert.Null(await NoticeAsync($"/events/{ev}"));

        var manage = await browser.GetPageAsync(Manage(ev));
        var mine = manage.QuerySelectorAll("#people .person").Single(p => p.TextContent.Contains("Pat (you)"));
        Assert.Contains("has viewed their assignment", mine.TextContent);
        var anns = manage.QuerySelectorAll("#people .person").Single(p => p.TextContent.Contains("Ann"));
        Assert.Contains("hasn't viewed their assignment", anns.TextContent);
    }

    [Fact]
    public async Task Updated_notes_are_marked_on_the_card_and_home_page_until_I_look()
    {
        var (ev, others) = await OpenEventAsync(3);
        var carol = others[2];
        await app.SealAsync(ev, Loop(me, carol, others[0], others[1]));
        async Task CarolEditsAsync() => await app.WithDbAsync(async db =>
        {
            var p = await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == carol);
            p.Notes += " More socks.";
            p.NotesVersion++;
            await db.SaveChangesAsync();
        });

        // Before I've ever looked, there's nothing to mark as updated.
        await CarolEditsAsync();
        Assert.Null((await browser.GetPageAsync($"/events/{ev}")).QuerySelector(".reveal .stamp"));
        Assert.Null((await browser.GetPageAsync("/")).QuerySelector(".slip-update"));

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/seen");
        await CarolEditsAsync();

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Equal("UPDATED", page.QuerySelector(".reveal .stamp")!.TextContent);
        Assert.Contains("Notes updated since you last looked", page.QuerySelector(".reveal")!.TextContent);
        var home = await browser.GetPageAsync("/");
        var update = home.QuerySelector(".slip-update")!;
        Assert.NotNull(update.QuerySelector(".dot"));
        Assert.Equal("Your recipient updated their notes", update.TextContent.Trim());
        // The home page never names the recipient: it isn't behind "Tap to reveal".
        Assert.DoesNotContain("Carol", home.Body!.TextContent);

        await browser.SubmitAsync(page, $"/events/{ev}/seen");
        Assert.Null((await browser.GetPageAsync($"/events/{ev}")).QuerySelector(".reveal .stamp"));
        Assert.Null((await browser.GetPageAsync("/")).QuerySelector(".slip-update"));
    }

    [Fact]
    public async Task Done_shopping_is_mine_to_set_after_the_draw_and_organizers_see_it()
    {
        var (ev, others) = await OpenEventAsync(3);

        // Not before the draw.
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/shopping", Form(("done", "true")));
        Assert.False(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == me).Select(p => p.DoneShopping).SingleAsync()));

        await app.SealAsync(ev, Loop([me, .. others]));
        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/shopping");
        Assert.True(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == me).Select(p => p.DoneShopping).SingleAsync()));
        var manage = await browser.GetPageAsync(Manage(ev));
        Assert.Contains(", done shopping", manage.QuerySelectorAll("#people .person").Single(p => p.TextContent.Contains("Pat (you)")).TextContent);

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/shopping");
        Assert.False(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == me).Select(p => p.DoneShopping).SingleAsync()));
    }

    [Fact]
    public async Task Redraw_puts_the_names_back_and_the_new_cards_say_so()
    {
        var (ev, others) = await OpenEventAsync(3);
        await app.SealAsync(ev, Loop([me, .. others]));
        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/seen");
        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/shopping");

        await ActAsync(ev, "redraw");
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Equal(0, await AssignmentCountAsync(ev));
        Assert.Contains("back in the hat", await NoticeAsync(Manage(ev)));
        Assert.False(await app.WithDbAsync(db => db.Participants.AnyAsync(p => p.EventId == ev && p.DoneShopping)));
        Assert.Contains("went back in the hat", (await browser.GetPageAsync($"/events/{ev}")).QuerySelector("main")!.TextContent);

        await ActAsync(ev, "draw");
        var card = (await browser.GetPageAsync($"/events/{ev}")).QuerySelector(".reveal")!;
        Assert.Contains("NAMES WERE DRAWN AGAIN", card.QuerySelector("summary")!.TextContent);
        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/seen");
        Assert.DoesNotContain("DRAWN AGAIN", (await browser.GetPageAsync($"/events/{ev}")).QuerySelector(".reveal summary")!.TextContent);
    }

    [Fact]
    public async Task Redraw_only_runs_after_a_draw()
    {
        var (ev, _) = await OpenEventAsync(3);
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/redraw");
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.False(await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).Select(e => e.Redrawn).SingleAsync()));
    }

    [Fact]
    public async Task A_slip_that_cant_be_read_under_the_current_key_says_to_ask_for_a_redraw()
    {
        var (ev, others) = await OpenEventAsync(3);
        await app.SealAsync(ev, Loop([me, .. others]));
        // As if the draw key had changed: no tag matches any more.
        await app.WithDbAsync(async db =>
        {
            foreach (var a in await db.Assignments.Where(a => a.EventId == ev).ToListAsync())
                a.GiverTag = RandomNumberGenerator.GetBytes(32);
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Null(page.QuerySelector(".reveal"));
        Assert.Contains("Can't find your slip", page.QuerySelector("#slip")!.TextContent);
        Assert.Contains("Ask an organizer to draw names again", page.QuerySelector("#slip")!.TextContent);
    }

    [Fact]
    public async Task Households_and_rules_stay_as_they_are_after_the_draw()
    {
        var (ev, others) = await OpenEventAsync(3);
        await app.SealAsync(ev, Loop([me, .. others]));

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Null(page.QuerySelector($"form[action='{Manage(ev)}/savehousehold']"));
        Assert.Null(page.QuerySelector($"form[action='{Manage(ev)}/addrule']"));
        Assert.Null(page.QuerySelector($"form[action='{Manage(ev)}/invite']"));

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/savehousehold", Form(("householdName", "X"), ("members", others[0].ToString())));
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addrule", Form(("personA", others[0].ToString()), ("personB", others[1].ToString())));
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/invite", Form(("email", "late@example.com")));
        Assert.Equal(0, await app.WithDbAsync(db => db.Households.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(db => db.Exclusions.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));

        // Adding or removing someone has to say whether to patch or redraw.
        var late = await app.PersonAsync("Late");
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addperson", Form(("personId", late.ToString())));
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/removeperson", Form(("personId", others[0].ToString())));
        Assert.Equal(((Guid[])[me, .. others]).Order(), (await app.ParticipantsAsync(ev)).Order());
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
    }
}
