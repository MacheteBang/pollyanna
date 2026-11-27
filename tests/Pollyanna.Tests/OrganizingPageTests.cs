using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The organizing page on "Tablecloth" (spec §7 Organizing an event, §12 Statuses): the head with
/// the switch or a "Home" link and the row of steps, the one next step on the cloth or what blocks
/// it, and the people taking part with words only where something is missing.
/// </summary>
public sealed class OrganizingPageTests : IAsyncLifetime
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

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    static readonly string[] Palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];

    static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>The one white panel on the cloth: its sentences, and its buttons and links (label, filled).</summary>
    static (List<string> Text, List<(string Label, bool Filled)> Controls) Panel(IDocument page)
    {
        var panel = Assert.Single(page.QuerySelectorAll(".cloth > .panel"));
        return (
            [.. panel.QuerySelectorAll("p").Select(p => Squash(p.TextContent))],
            [.. panel.QuerySelectorAll(".button, .link-button, a").Select(b => (Squash(b.TextContent), b.ClassList.Contains("button-fill")))]);
    }

    /// <summary>The rows under "Taking part": the name and the small words beside it.</summary>
    static List<(string Name, string Words)> TakingPart(IDocument page) =>
        [.. page.QuerySelectorAll("#people .rows > li").Select(li => (
            Squash(li.QuerySelector(".row-name")!.TextContent),
            Squash(li.QuerySelector(".row-words")?.TextContent ?? "")))];

    async Task<(Guid Ann, Guid Bob, Guid Cy)> ThreeOthersAsync()
    {
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var cy = await app.PersonAsync("Cy");
        return (ann, bob, cy);
    }

    // ---------- Head ----------

    [Fact]
    public async Task The_page_is_on_the_tablecloth_with_a_head_of_title_date_budget_and_the_steps()
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [me], [ann, bob], budget: "$20");

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
        Assert.Contains(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/tablecloth.css"));
        Assert.DoesNotContain(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/site.css"));
        Assert.Empty(page.QuerySelectorAll("[style], style"));
        var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
        Assert.All(hexes, h => Assert.Contains(h, Palette));

        var head = page.QuerySelector(".page-head")!;
        Assert.Equal("Cousins' Swap", Squash(head.QuerySelector("h1")!.TextContent));
        Assert.Contains("$20", head.QuerySelector(".page-head-line")!.TextContent);
        Assert.Contains("Dec 24, 2026", head.QuerySelector(".page-head-line")!.TextContent);
        Assert.Null(page.QuerySelector(".stamp"));
        var steps = head.QuerySelectorAll("ol.steps > li").ToList();
        Assert.Equal(["Draft", "Open", "Names drawn", "Revealed"], steps.Select(s => Squash(s.TextContent)));
        Assert.Equal("Open", Squash(Assert.Single(steps, s => s.GetAttribute("aria-current") == "step").TextContent));
    }

    [Theory]
    [InlineData(EventStatus.Draft, "Draft")]
    [InlineData(EventStatus.Open, "Open")]
    [InlineData(EventStatus.Drawn, "Names drawn")]
    [InlineData(EventStatus.Revealed, "Revealed")]
    public async Task The_current_step_is_marked(EventStatus status, string current)
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", status, [me], [ann, bob]);

        var page = await browser.GetPageAsync(Manage(ev));

        var steps = page.QuerySelectorAll("ol.steps > li").ToList();
        Assert.Equal(current, Squash(Assert.Single(steps, s => s.GetAttribute("aria-current") == "step").TextContent));
        Assert.Contains("step-now", Assert.Single(steps, s => s.GetAttribute("aria-current") == "step").ClassList);
    }

    [Fact]
    public async Task An_organizer_who_takes_part_gets_the_switch_and_one_who_doesnt_gets_a_home_link()
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var mine = await app.EventAsync("Mine too", EventStatus.Open, [me], [me, ann, bob]);
        var theirs = await app.EventAsync("Just organizing", EventStatus.Open, [me], [ann, bob]);

        var page = await browser.GetPageAsync(Manage(mine));
        var sw = page.QuerySelector(".page-head nav.switch")!;
        Assert.Equal(["My page", "Organizing"], sw.Children.Select(c => Squash(c.TextContent)));
        Assert.Equal($"/events/{mine}", sw.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("page", sw.Children[1].GetAttribute("aria-current"));
        Assert.Null(page.QuerySelector(".page-head-back"));

        page = await browser.GetPageAsync(Manage(theirs));
        Assert.Null(page.QuerySelector("nav.switch"));
        var back = page.QuerySelector(".page-head .page-head-back")!;
        Assert.Equal("Home", Squash(back.TextContent));
        Assert.Equal("/", back.GetAttribute("href"));
    }

    // ---------- The next step ----------

    [Fact]
    public async Task A_draft_says_only_organizers_see_it_and_opens_with_the_filled_button()
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [ann, bob]);

        var page = await browser.GetPageAsync(Manage(ev));

        var (text, controls) = Panel(page);
        Assert.Equal(["Only organizers can see this event. Open it so the people you've added can write their notes."], text);
        Assert.Equal([("Open the event", true)], controls);
        Assert.NotNull(page.QuerySelector($".panel form[action='{Manage(ev)}/open'] button.button-fill"));
    }

    [Fact]
    public async Task An_open_event_ready_to_draw_counts_people_and_notes()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var dee = await app.PersonAsync("Dee");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy, dee, me]);
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == ann)).Notes = "Socks";
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync(Manage(ev));

        var (text, controls) = Panel(page);
        Assert.Equal(["All 5 people are in and 1 has written notes. Draw names whenever you're ready."], text);
        Assert.Equal([("Draw names", true)], controls);
        Assert.NotNull(page.QuerySelector($".panel form[action='{Manage(ev)}/draw']"));
    }

    [Fact]
    public async Task Too_few_people_is_the_blocker_with_add_people()
    {
        var ann = await app.PersonAsync("Ann");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann]);

        var page = await browser.GetPageAsync(Manage(ev));

        var (text, controls) = Panel(page);
        Assert.Equal(["Can't draw yet: at least 3 people need to take part."], text);
        Assert.Equal([("Add people", true)], controls);
        Assert.Equal("#add-people", page.QuerySelector(".panel a.button-fill")!.GetAttribute("href"));
        Assert.NotNull(page.QuerySelector("#add-people form[data-add-people]"));
        Assert.Null(page.QuerySelector($"form[action='{Manage(ev)}/draw']"));
    }

    [Fact]
    public async Task Other_reasons_the_draw_is_refused_stand_in_the_same_place()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob, cy]);
        await app.HouseholdAsync(ev, "The Parks", ann, bob, cy);

        var page = await browser.GetPageAsync(Manage(ev));

        var (text, controls) = Panel(page);
        Assert.Equal(["Can't draw: The household The Parks has more than half the people taking part, so they can't all draw someone outside it."], text);
        Assert.Empty(controls);
        Assert.Null(page.QuerySelector($"form[action='{Manage(ev)}/draw']"));
    }

    [Fact]
    public async Task Drawn_counts_who_has_looked_and_reveals_with_the_filled_button()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy], date: new DateOnly(2020, 12, 24));
        await app.SealAsync(ev, Loop(ann, bob, cy));
        await app.UpdateAssignmentAsync(ev, ann, a => a.Viewed = true);

        var page = await browser.GetPageAsync(Manage(ev));

        var (text, controls) = Panel(page);
        Assert.Equal(["Names are drawn, and 1 of 3 people have looked. Reveal who drew whom once the gifts are opened."], text);
        Assert.Equal([("Reveal who drew whom", true)], controls);
        Assert.NotNull(page.QuerySelector($".panel form[action='{Manage(ev)}/reveal']"));
    }

    [Fact]
    public async Task Someone_wanting_to_leave_replaces_the_reveal_with_draw_names_again_or_keep_them_in()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy]);
        await app.SealAsync(ev, Loop(ann, bob, cy));
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == ann)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true)));

        var page = await browser.GetPageAsync(Manage(ev));

        var (text, controls) = Panel(page);
        Assert.Equal(["Ann wants to leave, and that needs a redraw. Drawing names again takes them out."], text);
        Assert.Equal([("Draw names again", true), ("Keep Ann in instead", false)], controls);
        Assert.Null(page.QuerySelector($".panel form[action='{Manage(ev)}/reveal']"));

        // Keeping them in drops the request, and the reveal is the next step again.
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/keepin");
        Assert.False(await app.WithDbAsync(db => db.Participants.AnyAsync(p => p.EventId == ev && p.WantsToLeave)));
        Assert.Contains(ann, await app.ParticipantsAsync(ev));
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        page = await browser.GetPageAsync(Manage(ev));
        Assert.Equal("Kept Ann in. Their request to leave is dropped.", Squash(page.QuerySelector(".notice")!.TextContent));
        Assert.Equal([("Reveal who drew whom", true)], Panel(page).Controls);
    }

    [Fact]
    public async Task Draw_names_again_from_the_blocker_takes_the_leaver_out()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var dee = await app.PersonAsync("Dee");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy, dee]);
        await app.SealAsync(ev, Loop(ann, bob, cy, dee));
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == ann)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true)));

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/redraw");

        Assert.DoesNotContain(ann, await app.ParticipantsAsync(ev));
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task Keeping_someone_in_is_for_organizers_of_a_drawn_event_only()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy]);
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == ann)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true)));

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/keepin", Form(("personId", ann.ToString())));

        Assert.True(await app.WithDbAsync(db => db.Participants.AnyAsync(p => p.EventId == ev && p.WantsToLeave)));
    }

    [Fact]
    public async Task Revealed_archives_with_the_filled_button()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Revealed, [me], [ann, bob, cy]);

        var page = await browser.GetPageAsync(Manage(ev));

        var (text, controls) = Panel(page);
        Assert.Equal("Everyone can see who drew whom. Archive the event when you're done with it, and it moves to past events.", text[0]);
        Assert.Contains(("Archive this event", true), controls);
        Assert.NotNull(page.QuerySelector($".panel form[action='{Manage(ev)}/archive']"));
    }

    [Fact]
    public async Task An_archived_event_has_no_next_step_and_no_cloth()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Archived, [me], [ann, bob, cy]);

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Null(page.QuerySelector(".cloth"));
        Assert.Null(page.QuerySelector(".button-fill"));
        Assert.Empty(page.QuerySelectorAll("ol.steps [aria-current]"));
    }

    // ---------- The people ----------

    [Fact]
    public async Task Before_the_draw_a_row_says_only_no_notes_yet()
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob]);
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == ann)).Notes = "Socks";
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal("Taking part (3)", Squash(page.QuerySelector("#people h2")!.TextContent));
        Assert.Equal([("Ann", ""), ("Bob", "no notes yet"), ("Pat (you)", "no notes yet")], TakingPart(page));
        // The three checkbox columns are gone.
        Assert.DoesNotContain("[x]", page.Body!.TextContent);
        Assert.DoesNotContain("[ ]", page.Body!.TextContent);
    }

    [Fact]
    public async Task Once_drawn_a_row_says_who_wants_to_leave_hasnt_looked_or_is_done_shopping()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var dee = await app.PersonAsync("Dee");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy, dee]);
        await app.SealAsync(ev, Loop(ann, bob, cy, dee));
        await app.UpdateAssignmentAsync(ev, bob, a => a.Viewed = true);
        await app.UpdateAssignmentAsync(ev, cy, a => a.Viewed = true);
        await app.UpdateAssignmentAsync(ev, dee, a => a.Viewed = true);
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == cy)).DoneShopping = true;
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == dee)).WantsToLeave = true;
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal([("Ann", "hasn't looked yet"), ("Bob", ""), ("Cy", "done shopping"), ("Dee", "wants to leave")], TakingPart(page));
    }

    [Fact]
    public async Task With_nobody_taking_part_the_list_says_so()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], []);

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal("Taking part (0)", Squash(page.QuerySelector("#people h2")!.TextContent));
        Assert.Contains("Nobody's taking part yet.", page.QuerySelector("#people")!.TextContent);
        Assert.Empty(TakingPart(page));
    }

    [Fact]
    public async Task Invitations_are_listed_with_take_back_then_an_outlined_add_people()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me]);
        await app.WithDbAsync(async db =>
        {
            db.Invitations.Add(new Invitation { EventId = ev, Email = "jo@pollyanna.example", EmailNormalized = "jo@pollyanna.example", InvitedBy = me });
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync(Manage(ev));

        var invited = page.QuerySelector("#invited")!;
        Assert.Equal("Invited, not signed in yet (1)", Squash(invited.QuerySelector("h2")!.TextContent));
        var row = Assert.Single(invited.QuerySelectorAll(".rows > li"));
        Assert.Contains("jo@pollyanna.example", row.TextContent);
        Assert.Equal("Take back", Squash(row.QuerySelector("button")!.TextContent));
        var add = page.QuerySelector("#add-people > summary")!;
        Assert.Equal("Add people", Squash(add.TextContent));
        Assert.DoesNotContain("button-fill", add.ClassList);

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/cancelinvite");
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector("#invited"));
    }

    [Fact]
    public async Task Once_drawn_invitations_by_email_wait()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy]);
        await app.SealAsync(ev, Loop(ann, bob, cy));

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Contains("Invitations by email wait until names are drawn again.", page.Body!.TextContent);
        Assert.Null(page.QuerySelector("#add-people"));
    }

    [Fact]
    public async Task No_string_on_the_reworked_part_says_hat_slip_or_stamp()
    {
        var (ann, bob, cy) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann]);

        var page = await browser.GetPageAsync(Manage(ev));

        foreach (var part in new[] { ".page-head", "#next-step", "#people", "#add-people" })
        {
            var text = page.QuerySelector(part)!.TextContent;
            Assert.DoesNotMatch(@"\b(hat|slips?|stamp)\b", text.ToLowerInvariant());
        }
    }
}
