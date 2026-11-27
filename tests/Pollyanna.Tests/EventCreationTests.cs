using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>Creating an event (spec §4, §7).</summary>
public sealed class EventCreationTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    static readonly MockIdentity Pat = new("pat", "Pat", "pat@example.com", true, null);

    static List<KeyValuePair<string, string>> Fields(string name = "Stockings 2026", string date = "2026-12-24", string takePart = "true",
        string budget = "$25", string previous = "", string depth = "1", params (string, string)[] more) =>
        [
            new("Name", name), new("BudgetText", budget), new("ExchangeDate", date),
            new("TakePart", takePart), new("PreviousEventId", previous), new("NoRepeatDepth", depth),
            .. more.Select(m => KeyValuePair.Create(m.Item1, m.Item2)),
        ];

    Task<Event> OnlyNewEventAsync(Guid creator, params Guid[] except) => app.WithDbAsync(db => db.Events
        .Include(e => e.Organizers).Include(e => e.Participants).Include(e => e.Households)
        .SingleAsync(e => e.CreatedBy == creator && !except.Contains(e.Id)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Creator_becomes_the_organizer_and_chooses_whether_to_take_part(bool takePart)
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;

        var response = await browser.SubmitOnAsync("/events/new", "/events/new", Fields(takePart: takePart ? "true" : "false"));

        var ev = await OnlyNewEventAsync(me);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/events/{ev.Id}/manage", response.Headers.Location!.OriginalString);
        Assert.Equal("Stockings 2026", ev.Name);
        Assert.Equal("$25", ev.BudgetText);
        Assert.Equal(new DateOnly(2026, 12, 24), ev.ExchangeDate);
        Assert.Equal(2026, ev.Year);
        Assert.Equal(EventStatus.Draft, ev.Status);
        Assert.Equal([me], ev.Organizers.Select(o => o.PersonId));
        Assert.Equal(takePart ? [me] : [], ev.Participants.Select(p => p.PersonId));
    }

    [Theory]
    [InlineData("", "2026-12-24", "Give the event a name.")]
    [InlineData("Stockings", "", "Pick the exchange date.")]
    [InlineData("Stockings", "1999-12-24", "between 2000 and 2100")]
    public async Task Invalid_details_show_the_form_again_with_what_was_typed(string name, string date, string message)
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;

        var response = await browser.SubmitOnAsync("/events/new", "/events/new", Fields(name: name, date: date, budget: "$40"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await TestBrowser.ParseAsync(response);
        Assert.Contains(message, page.QuerySelector(".notice-error")!.TextContent);
        Assert.Equal("$40", page.QuerySelector("input[name='BudgetText']")!.GetAttribute("value"));
        Assert.Equal(0, await app.WithDbAsync(db => db.Events.CountAsync()));
    }

    [Fact]
    public async Task Linking_an_earlier_event_brings_its_people_and_their_households()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var lee = await app.PersonAsync("Lee");
        var carol = await app.PersonAsync("Carol");
        var last = await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me, ann, bob, lee, carol], date: new DateOnly(2025, 12, 24));
        await app.WithDbAsync(async db =>
        {
            var parents = new Household { EventId = last, Name = "Mom & Dad" };
            var parks = new Household { EventId = last, Name = "The Parks" };
            var single = new Household { EventId = last, Name = "Only Carol" };
            db.Households.AddRange(parents, parks, single);
            foreach (var p in await db.Participants.Where(p => p.EventId == last).ToListAsync())
                p.HouseholdId = p.PersonId == ann || p.PersonId == bob ? parents.Id
                    : p.PersonId == lee || p.PersonId == me ? parks.Id
                    : single.Id;
            await db.SaveChangesAsync();
        });

        var form = await browser.GetPageAsync("/events/new");
        // The most recent event they organize is picked.
        Assert.Equal(last.ToString(), form.QuerySelector("select[name='PreviousEventId'] option[selected]")!.GetAttribute("value"));

        var response = await browser.SubmitAsync(form, "/events/new", Fields(previous: last.ToString(), depth: "2"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var ev = await OnlyNewEventAsync(me, last);
        Assert.Equal(last, ev.PreviousEventId);
        Assert.Equal(2, ev.NoRepeatDepth);
        Assert.Equal(new[] { me, ann, bob, lee, carol }.Order(), ev.Participants.Select(p => p.PersonId).Order());
        Assert.Equal(["Mom & Dad", "Only Carol", "The Parks"], ev.Households.Select(h => h.Name).Order());
        string HouseholdOf(Guid person) => ev.Households.Single(h => h.Id == ev.Participants.Single(p => p.PersonId == person).HouseholdId).Name;
        Assert.Equal("Mom & Dad", HouseholdOf(ann));
        Assert.Equal("Mom & Dad", HouseholdOf(bob));
        Assert.Equal("The Parks", HouseholdOf(lee));
        Assert.Equal("The Parks", HouseholdOf(me));
        Assert.Equal("Only Carol", HouseholdOf(carol));
    }

    [Fact]
    public async Task Not_taking_part_leaves_the_creator_out_of_the_linked_events_people()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var ann = await app.PersonAsync("Ann");
        var last = await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me, ann]);

        await browser.SubmitOnAsync("/events/new", "/events/new", Fields(previous: last.ToString(), takePart: "false"));

        var ev = await OnlyNewEventAsync(me, last);
        Assert.Equal([ann], ev.Participants.Select(p => p.PersonId));
    }

    [Fact]
    public async Task Starting_fresh_brings_nobody_along()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var ann = await app.PersonAsync("Ann");
        var last = await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me, ann]);

        await browser.SubmitOnAsync("/events/new", "/events/new", Fields(previous: ""));

        var ev = await OnlyNewEventAsync(me, last);
        Assert.Null(ev.PreviousEventId);
        Assert.Equal([me], ev.Participants.Select(p => p.PersonId));
    }

    [Fact]
    public async Task Only_events_the_creator_organizes_can_be_linked()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var other = await app.PersonAsync("Other");
        var notMine = await app.EventAsync("Their Swap", EventStatus.Archived, [other], [me, other]);

        var form = await browser.GetPageAsync("/events/new");
        Assert.Null(form.QuerySelector("select[name='PreviousEventId']"));

        var response = await browser.SubmitAsync(form, "/events/new", Fields(previous: notMine.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("an earlier event you organize", (await TestBrowser.ParseAsync(response)).QuerySelector(".notice-error")!.TextContent);
        Assert.Equal(1, await app.WithDbAsync(db => db.Events.CountAsync()));
    }

    [Fact]
    public async Task The_form_adds_nobody_else_and_invites_nobody()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var carol = await app.PersonAsync("Carol", "carol@example.com");
        var last = await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me, ann]);
        await app.EventAsync("Cousins", EventStatus.Revealed, [bob], [me, bob]);

        var form = (await browser.GetPageAsync("/events/new")).QuerySelector("form[action='/events/new']")!;
        // No add-people control: no ticklist of known people, no address field.
        Assert.Empty(form.QuerySelectorAll("[data-add-people], input[name=personIds], input[name=email], input[type=email]"));
        Assert.Empty(form.QuerySelectorAll("input[type=checkbox]:not([name=TakePart])"));

        // Fields the old form sent are ignored.
        var response = await browser.SubmitOnAsync("/events/new", "/events/new", Fields(previous: last.ToString(),
            more: [("personIds", bob.ToString()), ("email", "carol@example.com"), ($"copy-{last}", ann.ToString())]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var ev = await OnlyNewEventAsync(me, last);
        Assert.Equal(new[] { me, ann }.Order(), ev.Participants.Select(p => p.PersonId).Order());
        Assert.DoesNotContain(carol, ev.Participants.Select(p => p.PersonId));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(db => db.Notifications.CountAsync()));
    }

    [Fact]
    public async Task Creating_lands_on_the_organizing_page_with_a_notice()
    {
        var (browser, _) = await app.SignInAsync(Pat);
        using var _b = browser;

        var response = await browser.SubmitOnAsync("/events/new", "/events/new", Fields());

        var manage = await browser.GetPageAsync(response.Headers.Location!.OriginalString);
        Assert.Equal("Draft created. Add people below, then open it when you're ready.", manage.QuerySelector(".notice")!.TextContent.Trim());
    }

    [Fact]
    public async Task The_form_is_one_panel_on_the_cloth_with_create_event_filled()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me], date: new DateOnly(2025, 12, 24));

        var page = await browser.GetPageAsync("/events/new");

        Assert.Equal("New event", page.QuerySelector(".page-head h1")!.TextContent.Trim());
        var back = page.QuerySelector(".page-head a.page-head-back")!;
        Assert.Equal(("Home", "/"), (back.TextContent.Trim(), back.GetAttribute("href")));
        var form = Assert.Single(page.QuerySelectorAll(".cloth > form.panel[action='/events/new'][method=post]"));
        string LabelOf(string name)
        {
            var control = form.QuerySelector($"[name='{name}']:not([type=hidden])")!;
            return (form.QuerySelector($"label[for='{control.Id}']") ?? control.Closest("label"))!.TextContent.Trim();
        }
        string[] names = ["Name", "ExchangeDate", "BudgetText", "TakePart", "PreviousEventId", "NoRepeatDepth"];
        Assert.Equal(["Name", "Exchange date", "Budget", "Yes, I'm taking part too", "Same group as an earlier event", "Don't repeat who gave to whom"],
            names.Select(LabelOf));
        // The controls come in that order too.
        Assert.Equal(names, form.QuerySelectorAll("input:not([type=hidden]), select").Select(c => c.GetAttribute("name")));
        var takePart = form.QuerySelector("input[name=TakePart][type=checkbox]")!;
        Assert.Equal("true", takePart.GetAttribute("value"));
        Assert.True(takePart.HasAttribute("checked"));
        Assert.Equal(["No, start with nobody", "Stockings 2025, December 2025"],
            form.QuerySelectorAll("select[name=PreviousEventId] option").Select(o => o.TextContent.Trim()));
        Assert.Equal(["From the last event", "From the last 2 events", "From the last 3 events"],
            form.QuerySelectorAll("select[name=NoRepeatDepth] option").Select(o => o.TextContent.Trim()));
        // The script hides the don't-repeat choice until an earlier event is picked; without it, it shows.
        Assert.NotNull(form.QuerySelector("select[name=PreviousEventId][data-previous-select]"));
        var depth = form.QuerySelector("select[name=NoRepeatDepth]")!.Closest("[data-needs-previous]")!;
        Assert.False(depth.HasAttribute("hidden"));

        var create = Assert.Single(form.QuerySelectorAll(".button"));
        Assert.Equal("Create event", create.TextContent.Trim());
        Assert.Equal("submit", create.GetAttribute("type"));
        Assert.True(create.ClassList.Contains("button-fill"));
        Assert.Equal("It starts as a draft only organizers can see. You add people next.",
            create.NextElementSibling!.TextContent.Trim());
        Assert.DoesNotContain("slip", form.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" hat", form.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_an_earlier_event_there_is_no_group_choice()
    {
        var (browser, _) = await app.SignInAsync(Pat);
        using var _b = browser;

        var form = (await browser.GetPageAsync("/events/new")).QuerySelector("form.panel")!;

        Assert.Null(form.QuerySelector("select[name=PreviousEventId]"));
        Assert.Null(form.QuerySelector("select[name=NoRepeatDepth]"));
        Assert.Equal("Create event", form.QuerySelector(".button-fill")!.TextContent.Trim());
    }

    [Fact]
    public async Task An_unticked_take_part_box_sends_false()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;

        var form = (await browser.GetPageAsync("/events/new")).QuerySelector("form.panel")!;
        // A browser sends nothing for an unticked box, so a hidden "false" follows it.
        Assert.Equal("false", form.QuerySelector("input[type=hidden][name=TakePart]")!.GetAttribute("value"));
        await browser.SubmitOnAsync("/events/new", "/events/new",
        [
            new("Name", "Stockings"), new("BudgetText", ""), new("ExchangeDate", "2026-12-24"), new("TakePart", "false"),
        ]);

        var ev = await OnlyNewEventAsync(me);
        Assert.Empty(ev.Participants);
    }

    [Fact]
    public async Task The_page_is_on_the_tablecloth_look()
    {
        var (browser, _) = await app.SignInAsync(Pat);
        using var _b = browser;

        var page = await browser.GetPageAsync("/events/new");

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
        Assert.Empty(page.QuerySelectorAll("[style], style"));
    }

    [Fact]
    public async Task The_manage_pages_ticklist_shows_everyone_the_organizer_knows_unticked()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var ann = await app.PersonAsync("Ann");
        await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me, ann]);
        var draft = await app.EventAsync("Stockings 2026", EventStatus.Draft, [me], [me]);

        var page = await browser.GetPageAsync($"/events/{draft}/manage");

        var annBox = page.QuerySelector($"input[name=personIds][value='{ann}']")!;
        Assert.False(annBox.HasAttribute("checked") || annBox.HasAttribute("disabled"));
        Assert.False(annBox.ParentElement!.HasAttribute("hidden"));
    }

    [Fact]
    public async Task The_new_draft_shows_on_the_home_page_with_a_countdown_slot()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        await browser.SubmitOnAsync("/events/new", "/events/new", Fields(name: "Cousins' Swap", budget: "$15", date: "2026-12-20"));

        var home = await browser.GetPageAsync("/");
        var slip = home.QuerySelector(".cloth .panel")!;
        Assert.Equal("Cousins' Swap", slip.QuerySelector(".panel-title")!.TextContent);
        Assert.Contains("draft, only organizers can see it", slip.TextContent);
        Assert.Equal("2026-12-20", slip.QuerySelector("time")!.GetAttribute("datetime"));
        Assert.Equal("Dec 20, 2026", slip.QuerySelector("time")!.TextContent);
        var countdown = slip.QuerySelector("[data-countdown]")!;
        Assert.Equal("2026-12-20", countdown.GetAttribute("data-countdown"));
        Assert.True(countdown.HasAttribute("hidden")); // the script fills it in from the viewer's date
    }
}
