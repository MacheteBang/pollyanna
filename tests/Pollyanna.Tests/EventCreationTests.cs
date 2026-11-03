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
    public async Task Linking_an_earlier_event_copies_the_ticked_people_and_their_households()
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
            var gone = new Household { EventId = last, Name = "Only Carol" };
            db.Households.AddRange(parents, parks, gone);
            foreach (var p in await db.Participants.Where(p => p.EventId == last).ToListAsync())
                p.HouseholdId = p.PersonId == ann || p.PersonId == bob ? parents.Id
                    : p.PersonId == lee || p.PersonId == me ? parks.Id
                    : gone.Id;
            await db.SaveChangesAsync();
        });

        var form = await browser.GetPageAsync("/events/new");
        // The most recent event they organize is picked, with everyone else ticked.
        Assert.Equal(last.ToString(), form.QuerySelector("select[name='PreviousEventId'] option[selected]")!.GetAttribute("value"));
        Assert.Equal(4, form.QuerySelectorAll($"input[name='copy-{last}'][checked]").Length);

        // Carol isn't joining this year.
        var response = await browser.SubmitAsync(form, "/events/new", Fields(previous: last.ToString(), depth: "2",
            more: [($"copy-{last}", ann.ToString()), ($"copy-{last}", bob.ToString()), ($"copy-{last}", lee.ToString())]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var ev = await OnlyNewEventAsync(me, last);
        Assert.Equal(last, ev.PreviousEventId);
        Assert.Equal(2, ev.NoRepeatDepth);
        Assert.Equal(new[] { me, ann, bob, lee }.Order(), ev.Participants.Select(p => p.PersonId).Order());
        Assert.Equal(["Mom & Dad", "The Parks"], ev.Households.Select(h => h.Name).Order());
        string HouseholdOf(Guid person) => ev.Households.Single(h => h.Id == ev.Participants.Single(p => p.PersonId == person).HouseholdId).Name;
        Assert.Equal("Mom & Dad", HouseholdOf(ann));
        Assert.Equal("Mom & Dad", HouseholdOf(bob));
        Assert.Equal("The Parks", HouseholdOf(lee));
        Assert.Equal("The Parks", HouseholdOf(me));
    }

    [Fact]
    public async Task Starting_fresh_ignores_ticks_under_an_earlier_event()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var ann = await app.PersonAsync("Ann");
        var last = await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me, ann]);

        await browser.SubmitOnAsync("/events/new", "/events/new", Fields(previous: "", more: [($"copy-{last}", ann.ToString())]));

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

        var response = await browser.SubmitAsync(form, "/events/new", Fields(previous: notMine.ToString(), more: [($"copy-{notMine}", other.ToString())]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("an earlier event you organize", (await TestBrowser.ParseAsync(response)).QuerySelector(".notice-error")!.TextContent);
        Assert.Equal(1, await app.WithDbAsync(db => db.Events.CountAsync()));
    }

    [Fact]
    public async Task People_ticked_from_the_earlier_event_must_have_been_in_it()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        var stranger = await app.PersonAsync("Stranger");
        var last = await app.EventAsync("Stockings 2025", EventStatus.Archived, [me], [me]);

        await browser.SubmitOnAsync("/events/new", "/events/new", Fields(previous: last.ToString(), more: [($"copy-{last}", stranger.ToString())]));

        var ev = await OnlyNewEventAsync(me, last);
        Assert.Equal([me], ev.Participants.Select(p => p.PersonId));
    }

    [Fact]
    public async Task The_new_draft_shows_on_the_home_page_with_a_countdown_slot()
    {
        var (browser, me) = await app.SignInAsync(Pat);
        using var _b = browser;
        await browser.SubmitOnAsync("/events/new", "/events/new", Fields(name: "Cousins' Swap", budget: "$15", date: "2026-12-20"));

        var home = await browser.GetPageAsync("/");
        var slip = home.QuerySelector("a.slip")!;
        Assert.Equal("Cousins' Swap", slip.QuerySelector(".slip-title")!.TextContent);
        Assert.Equal("DRAFT", slip.QuerySelector(".stamp")!.TextContent);
        Assert.Equal("2026-12-20", slip.QuerySelector("time")!.GetAttribute("datetime"));
        Assert.Equal("Dec 20, 2026", slip.QuerySelector("time")!.TextContent);
        var countdown = slip.QuerySelector("[data-countdown]")!;
        Assert.Equal("2026-12-20", countdown.GetAttribute("data-countdown"));
        Assert.True(countdown.HasAttribute("hidden")); // the script fills it in from the viewer's date
    }
}
