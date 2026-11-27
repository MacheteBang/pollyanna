using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// What the server gives the page's script to update home and the organizing page live (spec §16
/// "What each page does live"). The page's shape and the quiet line for arriving at it are on
/// <c>main</c> (<c>data-live-shape</c>, <c>data-live-line</c>): a refetch with another shape is a
/// state change. Each region that patches in place carries <c>data-live</c> and a stable id; a
/// <c>data-live-list</c> holds regions that come and go (home's panels). The script itself is
/// checked by hand (§16 Testing).
/// </summary>
public sealed class LivePagesTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!;
    Guid patId, ann, bob, cy;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (pat, patId) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        ann = await app.PersonAsync("Ann", "ann@example.com");
        bob = await app.PersonAsync("Bob", "bob@example.com");
        cy = await app.PersonAsync("Cy", "cy@example.com");
    }

    public async Task DisposeAsync()
    {
        pat.Dispose();
        await app.DisposeAsync();
    }

    static List<string> Regions(IDocument page) =>
        [.. page.QuerySelectorAll("main [data-live]").Select(r => r.Id ?? "(no id)")];

    [Fact]
    public async Task Home_holds_each_panel_as_a_region_named_by_its_event_in_one_list()
    {
        var first = await app.EventAsync("Stockings", EventStatus.Open, [patId], [patId, ann]);
        var second = await app.EventAsync("Cousins", EventStatus.Drawn, [ann], [patId, ann, bob]);

        var page = await pat.GetPageAsync("/");

        Assert.Equal("home", page.QuerySelector("main")!.GetAttribute("data-live-shape"));
        var list = Assert.Single(page.QuerySelectorAll("main [data-live-list]"));
        Assert.Equal("home-events", list.Id);
        Assert.Equivalent(new[] { $"event-{first}", $"event-{second}" },
            list.Children.Where(c => c.HasAttribute("data-live")).Select(c => c.Id), strict: true);
    }

    [Fact]
    public async Task Empty_home_is_a_shape_of_its_own()
    {
        var page = await pat.GetPageAsync("/");

        Assert.Equal("home-empty", page.QuerySelector("main")!.GetAttribute("data-live-shape"));
        Assert.Equal("", page.QuerySelector("main")!.GetAttribute("data-live-line") ?? "");
    }

    public static TheoryData<EventStatus, bool, string, string> OrganizingShapes => new()
    {
        { EventStatus.Draft, false, "manage-draft", "" },
        { EventStatus.Open, false, "manage-open", "The event is open." },
        { EventStatus.Open, true, "manage-open", "The event is open." },
        { EventStatus.Drawn, false, "manage-drawn", "Names have been drawn." },
        { EventStatus.Drawn, true, "manage-drawn", "Names have been drawn again." },
        { EventStatus.Revealed, false, "manage-revealed", "Everyone's been revealed." },
        { EventStatus.Archived, false, "manage-archived", "This event was archived." },
    };

    [Theory]
    [MemberData(nameof(OrganizingShapes))]
    public async Task Organizing_names_its_shape_by_status_with_the_line_for_arriving_there(
        EventStatus status, bool redrawn, string shape, string line)
    {
        var ev = await app.EventAsync("Stockings", status, [patId], [ann, bob, cy]);
        if (status is not (EventStatus.Draft or EventStatus.Open))
            await app.SealAsync(ev, Loop(ann, bob, cy));
        await app.WithDbAsync(async db =>
        {
            var e = (await db.Events.FindAsync(ev))!;
            e.Status = status;
            e.Redrawn = redrawn;
            await db.SaveChangesAsync();
        });

        var main = (await pat.GetPageAsync($"/events/{ev}/manage")).QuerySelector("main")!;

        Assert.Equal(shape, main.GetAttribute("data-live-shape"));
        Assert.Equal(line, main.GetAttribute("data-live-line") ?? "");
    }

    public static TheoryData<EventStatus, string[]> OrganizingRegions => new()
    {
        { EventStatus.Open, ["event-head", "next-step", "people", "live-invited", "add-people",
            "rules-summary", "rules-body", "settings-summary", "details", "organizers", "changes"] },
        { EventStatus.Drawn, ["event-head", "next-step", "people", "live-invited",
            "rules-summary", "rules-body", "settings-summary", "details", "organizers", "changes"] },
    };

    [Theory]
    [MemberData(nameof(OrganizingRegions))]
    public async Task Organizing_marks_the_regions_that_patch_in_place(EventStatus status, string[] regions)
    {
        var ev = await app.EventAsync("Stockings", status, [patId], [ann, bob, cy]);
        if (status is not EventStatus.Open)
            await app.SealAsync(ev, Loop(ann, bob, cy));
        await app.WithDbAsync(async db =>
        {
            (await db.Events.FindAsync(ev))!.Status = status;
            await db.SaveChangesAsync();
        });

        var page = await pat.GetPageAsync($"/events/{ev}/manage");

        Assert.Equal(regions, Regions(page));
        // The invitations' region is there to fill even with none waiting.
        Assert.Empty(page.QuerySelector("#live-invited")!.Children);
    }

    async Task<Guid> EventInAsync(EventStatus status, bool redrawn = false)
    {
        var ev = await app.EventAsync("Stockings", status, [ann], [patId, ann, bob]);
        if (status is not (EventStatus.Draft or EventStatus.Open))
            await app.SealAsync(ev, Loop(patId, ann, bob));
        await app.WithDbAsync(async db =>
        {
            var e = (await db.Events.FindAsync(ev))!;
            e.Status = status;
            e.Redrawn = redrawn;
            await db.SaveChangesAsync();
        });
        return ev;
    }

    public static TheoryData<EventStatus, bool, string, string> EventShapes => new()
    {
        { EventStatus.Open, false, "event-open", "The event is open." },
        { EventStatus.Open, true, "event-open", "The event is open." },
        { EventStatus.Drawn, false, "event-drawn", "Names have been drawn." },
        { EventStatus.Drawn, true, "event-drawn", "Names have been drawn again." },
        { EventStatus.Revealed, false, "event-revealed", "Everyone's been revealed." },
        { EventStatus.Archived, false, "event-archived", "This event was archived." },
    };

    [Theory]
    [MemberData(nameof(EventShapes))]
    public async Task The_event_page_names_its_shape_by_status_with_the_line_for_arriving_there(
        EventStatus status, bool redrawn, string shape, string line)
    {
        var ev = await EventInAsync(status, redrawn);

        var main = (await pat.GetPageAsync($"/events/{ev}")).QuerySelector("main")!;

        Assert.Equal(shape, main.GetAttribute("data-live-shape"));
        Assert.Equal(line, main.GetAttribute("data-live-line") ?? "");
    }

    public static TheoryData<EventStatus, string[]> EventRegions => new()
    {
        // Before the draw: the details, your own notes (saved in another tab), "Taking part".
        { EventStatus.Open, ["event-head", "your-notes", "taking-part"] },
        // After it: the recipient's notes under "You're giving to", "Done shopping", your notes and the people.
        // The lines over the gift and the question box (with its thread and closed line) too, for
        // what a giver's or recipient's own content frame brings (#104).
        { EventStatus.Drawn, ["event-head", "live-asked", "live-gift-again", "their-notes", "ask", "ask-thread", "ask-closed",
            "shopping", "live-questions-link", "your-notes", "taking-part"] },
        // Over: state changes only (the head is the same part in every state, though nothing in it changes now).
        { EventStatus.Revealed, ["event-head"] },
        { EventStatus.Archived, ["event-head"] },
    };

    [Theory]
    [MemberData(nameof(EventRegions))]
    public async Task The_event_page_marks_the_regions_that_patch_in_place(EventStatus status, string[] regions)
    {
        var ev = await EventInAsync(status);

        var page = await pat.GetPageAsync($"/events/{ev}");

        Assert.Equal(regions, Regions(page));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/untie")]
    public async Task The_gift_is_never_part_of_a_region_so_a_patch_keeps_it_tied_or_untied(string untie)
    {
        var ev = await EventInAsync(EventStatus.Drawn);

        var gift = (await pat.GetPageAsync($"/events/{ev}{untie}")).QuerySelector("#gift")!;

        Assert.Null(gift.Closest("[data-live]"));
        // Only the parts under the name patch, inside it.
        Assert.Equal(["their-notes", "ask", "ask-thread", "ask-closed", "shopping"], gift.QuerySelectorAll("[data-live]").Select(r => r.Id ?? "").ToList());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_questions_page_is_live_with_its_conversation_a_region_inside_the_rest(bool asked)
    {
        var ev = await EventInAsync(EventStatus.Drawn);
        if (asked)
            await app.MessageAsync(ev, bob, MessageSender.Giver, "Socks?");

        var page = await pat.GetPageAsync($"/events/{ev}/questions");

        Assert.Equal("questions-drawn", page.QuerySelector("main")!.GetAttribute("data-live-shape"));
        Assert.Equal(asked ? ["questions", "thread"] : ["questions"], Regions(page));
    }

    [Fact]
    public async Task A_live_refetch_brings_the_recipients_new_notes_and_the_updated_line_under_the_name()
    {
        var (rae, raeId) = await app.SignInAsync(new MockIdentity("rae", "Rae", "rae@example.com", true, null));
        var ev = await app.EventAsync("Stockings", EventStatus.Drawn, [ann], [patId, raeId, bob]);
        await app.SealAsync(ev, Loop(patId, raeId, bob));
        await app.UpdateAssignmentAsync(ev, patId, a => a.Viewed = true);
        using var tab = await pat.OpenLiveAsync();
        await app.Live.SettleAsync();
        tab.Clear();

        await rae.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", "Blue socks, size 9")));
        await app.Live.SettleAsync();
        var notes = (await pat.GetPageAsync($"/events/{ev}", live: true)).QuerySelector("#their-notes")!;

        Assert.Equal([new LiveHintData(ev, nameof(Pollyanna.Happenings.HappeningKind.NotesEdited))], tab.Hints);
        Assert.Contains("Blue socks, size 9", notes.TextContent);
        Assert.Contains("Notes updated since you last looked", notes.TextContent);
        rae.Dispose();
    }
}
