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
}
