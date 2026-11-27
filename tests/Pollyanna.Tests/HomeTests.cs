using AngleSharp.Dom;
using Microsoft.EntityFrameworkCore;
using Pollyanna.Data;
using Pollyanna.Tests.Support;

namespace Pollyanna.Tests;

/// <summary>
/// Home in the "Tablecloth" identity (spec §7 Home, Empty home, Past events): one panel per
/// event with the one thing waiting on the viewer, the empty home in two halves, and the plain
/// list of past events.
/// </summary>
public sealed class HomeTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    /// <summary>Each event's panel on the cloth: its name, and its waiting line and where it links (null without one).</summary>
    static List<(string Name, string? Line, string? Href)> Panels(IDocument page) =>
        [.. page.QuerySelectorAll(".cloth > .panel").Select(p =>
        {
            var line = p.QuerySelector(".waiting");
            return (p.QuerySelector(".panel-title")!.TextContent.Trim(), line?.TextContent.Trim(), line?.GetAttribute("href"));
        })];

    async Task<(TestBrowser Browser, Guid Me, Guid A, Guid B)> MeAndTwoAsync()
    {
        var (browser, me) = await app.SignInAsync(new("me", "Sam Rivera", "sam@pollyanna.example", true, null));
        return (browser, me, await app.PersonAsync("Ana"), await app.PersonAsync("Kofi"));
    }

    [Fact]
    public async Task Open_without_notes_asks_for_notes_and_links_to_the_event()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [a], [me, a, b]);

        var page = await browser.GetPageAsync("/");

        Assert.Equal([("Cousins' Swap", "Write your notes", $"/events/{ev}")], Panels(page));
    }

    Task WriteNotesAsync(Guid ev, Guid person, string notes = "Tea, please") =>
        app.WithDbAsync(async db =>
        {
            var p = await db.Participants.SingleAsync(x => x.EventId == ev && x.PersonId == person);
            p.Notes = notes;
            p.NotesVersion++;
            await db.SaveChangesAsync();
        });

    async Task<string?> LineAsync(TestBrowser browser) => Assert.Single(Panels(await browser.GetPageAsync("/"))).Line;

    [Fact]
    public async Task Open_with_notes_written_says_there_is_nothing_to_do_yet()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [a], [me, a, b]);
        await WriteNotesAsync(ev, me);

        Assert.Equal([("Cousins' Swap", "Nothing to do until names are drawn", $"/events/{ev}")], Panels(await browser.GetPageAsync("/")));
    }

    [Fact]
    public async Task Drawn_asks_to_see_who_you_are_giving_to_until_looked_then_waits_on_nothing()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [a], [me, a, b]);
        await app.SealAsync(ev, Seed.Loop(me, a, b));

        Assert.Equal([("Cousins' Swap", "See who you're giving to", $"/events/{ev}")], Panels(await browser.GetPageAsync("/")));

        await app.UpdateAssignmentAsync(ev, me, x => x.Viewed = true);
        var page = await browser.GetPageAsync("/");
        Assert.Equal([("Cousins' Swap", null, null)], Panels(page));
        // With nothing waiting, the event's name still opens it.
        Assert.Equal($"/events/{ev}", page.QuerySelector(".panel-title a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task A_changed_recipient_and_updated_notes_are_said_without_naming_anyone()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [a], [me, a, b]);
        await app.SealAsync(ev, Seed.Loop(me, a, b));
        await app.UpdateAssignmentAsync(ev, me, x => x.Viewed = true);

        await WriteNotesAsync(ev, a);
        Assert.Equal("Your recipient updated their notes", await LineAsync(browser));

        await app.UpdateAssignmentAsync(ev, me, x => x.RecipientChanged = true);
        Assert.Equal("You're giving to someone new", await LineAsync(browser));

        var text = (await browser.GetPageAsync("/")).QuerySelector("main")!.TextContent;
        Assert.DoesNotContain("Ana", text);
        Assert.DoesNotContain("Kofi", text);
    }

    [Fact]
    public async Task An_unread_question_comes_first_and_links_to_the_conversation()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [me], [me, a, b]);
        await app.SealAsync(ev, Seed.Loop(me, a, b));
        await app.UpdateAssignmentAsync(ev, me, x => x.RecipientChanged = true);
        await app.WithDbAsync(async db => { (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == a)).WantsToLeave = true; await db.SaveChangesAsync(); });
        // b gives to me and asks something.
        await app.MessageAsync(ev, b, MessageSender.Giver, "Do you like socks?");

        Assert.Equal([("Cousins' Swap", "Your giver asked you something", $"/events/{ev}/questions")], Panels(await browser.GetPageAsync("/")));
    }

    [Fact]
    public async Task A_changed_recipient_comes_before_an_organizers_line_which_comes_before_updated_notes()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [me], [me, a, b]);
        await app.SealAsync(ev, Seed.Loop(me, a, b));
        await app.UpdateAssignmentAsync(ev, me, x => x.Viewed = true);
        await WriteNotesAsync(ev, a);
        await app.WithDbAsync(async db => { (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == b)).WantsToLeave = true; await db.SaveChangesAsync(); });

        Assert.Equal([("Cousins' Swap", "Someone wants to leave", $"/events/{ev}/manage")], Panels(await browser.GetPageAsync("/")));

        await app.UpdateAssignmentAsync(ev, me, x => x.RecipientChanged = true);
        Assert.Equal("You're giving to someone new", await LineAsync(browser));
    }

    [Fact]
    public async Task Organizers_see_ready_to_open_and_ready_to_draw_names_linking_to_the_organizing_page()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var draft = await app.EventAsync("Draft swap", EventStatus.Draft, [me], [a, b], date: new DateOnly(2026, 12, 1));
        var ready = await app.EventAsync("Ready swap", EventStatus.Open, [me], [me, a, b], date: new DateOnly(2026, 12, 2));
        var small = await app.EventAsync("Small swap", EventStatus.Open, [me], [a, b], date: new DateOnly(2026, 12, 3));

        Assert.Equal(
            [
                ("Draft swap", "Ready to open", $"/events/{draft}/manage"),
                // The organizer's line comes before their own "Write your notes".
                ("Ready swap", "Ready to draw names", $"/events/{ready}/manage"),
                // Too few people to draw, and they don't take part: nothing waits on them.
                ("Small swap", null, null),
            ],
            Panels(await browser.GetPageAsync("/")));
        Assert.Equal($"/events/{small}/manage", (await browser.GetPageAsync("/")).QuerySelectorAll(".panel-title a")[2].GetAttribute("href"));
    }

    [Fact]
    public async Task Date_and_countdown_come_second_then_status_budget_and_the_organizer_line_in_small_text_with_no_dot()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        await app.EventAsync("Cousins' Swap", EventStatus.Open, [me], [me, a, b], budget: "$40");

        var page = await browser.GetPageAsync("/");
        var panel = page.QuerySelector(".cloth > .panel")!;
        var paragraphs = panel.QuerySelectorAll("p").ToList();

        Assert.Equal("2026-12-24", paragraphs[0].QuerySelector("time")!.GetAttribute("datetime"));
        Assert.NotNull(paragraphs[0].QuerySelector("[data-countdown]"));
        Assert.Equal(["names not drawn yet · $40", "You organize · 3 people"], paragraphs.Skip(1).Select(p => p.TextContent.Trim()));
        Assert.All(paragraphs.Skip(1), p => Assert.Contains("small", p.ClassList));
        Assert.Empty(page.QuerySelectorAll(".dot, .badge, .stamp"));
        // One panel per event, and nothing else, on the one cloth.
        Assert.Single(page.QuerySelectorAll(".cloth"));
    }

    [Fact]
    public async Task With_events_new_event_is_a_quiet_action_then_past_events_and_there_is_no_about_text()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        await app.EventAsync("Cousins' Swap", EventStatus.Open, [a], [me, a, b]);

        var page = await browser.GetPageAsync("/");

        var below = page.QuerySelectorAll(".cloth ~ * a").Select(x => (x.TextContent.Trim(), x.GetAttribute("href"), x.ClassList.Contains("button-fill"))).ToList();
        Assert.Equal([("New event", "/events/new", false), ("Past events", "/past", false)], below);
        Assert.Null(page.QuerySelector(".about"));
        Assert.DoesNotContain("Philadelphia", page.Body!.TextContent);
    }

    const string LongAbout = "Every family has a version of this: names on folded slips, a hat going round the table, someone squinting at the handwriting. Around Philadelphia they call it a Pollyanna. This is that, minus the squinting.";

    [Fact]
    public async Task The_empty_home_is_one_panel_in_two_equal_halves_then_the_long_about_text_and_past_events()
    {
        var (browser, _) = await app.SignInAsync(new("sam", "Sam", "sam@pollyanna.example", true, null));
        using var _b = browser;

        var page = await browser.GetPageAsync("/");

        Assert.Equal("No events yet", page.QuerySelector("h1")!.TextContent.Trim());
        var panel = Assert.Single(page.QuerySelectorAll(".cloth > .panel"));
        Assert.Equal(["I was invited", "I'm organizing one"], panel.QuerySelectorAll("h2").Select(h => h.TextContent.Trim()));
        Assert.Contains("sam@pollyanna.example", panel.TextContent);
        // Equal weight: both buttons outlined, none filled.
        Assert.Equal([("Copy", false), ("New event", false)],
            panel.QuerySelectorAll(".button").Select(x => (x.TextContent.Trim(), x.ClassList.Contains("button-fill"))));
        Assert.Equal("sam@pollyanna.example", panel.QuerySelector("button[data-copy]")!.GetAttribute("data-copy"));
        Assert.Equal("/events/new", panel.QuerySelector("a.button")!.GetAttribute("href"));
        Assert.Empty(page.QuerySelectorAll(".button-fill"));

        Assert.Equal(LongAbout, page.QuerySelector(".about")!.TextContent.Trim());
        var after = page.QuerySelectorAll(".cloth ~ *").SelectMany(x => x.QuerySelectorAll("a")).Select(a => (a.TextContent.Trim(), a.GetAttribute("href")));
        Assert.Equal([("Past events", "/past")], after);
    }

    [Fact]
    public async Task Without_the_script_the_copy_button_stays_hidden_and_the_address_is_still_there()
    {
        var (browser, _) = await app.SignInAsync(new("sam", "Sam", "sam@pollyanna.example", true, null));
        using var _b = browser;

        var page = await browser.GetPageAsync("/");

        Assert.True(page.QuerySelector("button[data-copy]")!.HasAttribute("hidden"));
        Assert.Contains("sam@pollyanna.example", page.QuerySelector(".panel")!.TextContent);
    }

    static List<(string Text, string? Href)> PastRows(IDocument page) =>
        [.. page.QuerySelectorAll("main li a").Select(a => (string.Join(" | ", a.Children.Select(c => c.TextContent.Trim())), a.GetAttribute("href")))];

    [Fact]
    public async Task Past_events_is_a_plain_list_of_links_with_name_date_budget_and_the_viewers_part()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var swap = await app.EventAsync("Cousins' Swap", EventStatus.Archived, [a], [me, a, b], budget: "$40", date: new DateOnly(2025, 12, 19));
        await app.WithDbAsync(async db => { (await db.Events.SingleAsync(e => e.Id == swap)).PairingsRevealed = true; await db.SaveChangesAsync(); });
        var club = await app.EventAsync("Book club", EventStatus.Archived, [me], [a, b], budget: "", date: new DateOnly(2026, 1, 10));

        var page = await browser.GetPageAsync("/past");

        Assert.Equal("Past events", page.QuerySelector("h1")!.TextContent.Trim());
        Assert.Equal(
            [
                ("Book club | Jan 10, 2026 | You organized · 2 people", $"/events/{club}"),
                ("Cousins' Swap | Dec 19, 2025 · $40 | You took part · 3 people · pairings revealed", $"/events/{swap}"),
            ],
            PastRows(page));
        Assert.Null(page.QuerySelector(".cloth"));
        Assert.DoesNotContain("over", page.QuerySelector("main ul")!.TextContent);
        Assert.Empty(page.QuerySelectorAll(".stamp"));
        Assert.Equal("/", page.QuerySelector(".page-head-back")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Past_events_says_so_when_there_are_none()
    {
        var (browser, _) = await app.SignInAsync(new("sam", "Sam", "sam@pollyanna.example", true, null));
        using var _b = browser;

        var page = await browser.GetPageAsync("/past");

        Assert.Contains("No past events yet. Events move here once an organizer archives them.", page.QuerySelector("main")!.TextContent);
        Assert.Null(page.QuerySelector(".cloth"));
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData("/", true)]
    [InlineData("/past", true)]
    public async Task Home_and_past_events_are_on_the_tablecloth_light_only_with_visible_focus(string path, bool withEvents)
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        if (withEvents)
        {
            await app.EventAsync("Cousins' Swap", EventStatus.Open, [a], [me, a, b]);
            await app.EventAsync("Old swap", EventStatus.Archived, [a], [me, a, b]);
        }

        var page = await browser.GetPageAsync(path);

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
        Assert.Empty(page.QuerySelectorAll("[style], style"));
        Assert.All(System.Text.RegularExpressions.Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()),
            h => Assert.Contains(h, new[] { "#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019" }));
        // Nothing turns the focus ring off (it is set once, on :focus-visible, in tablecloth.css).
        Assert.DoesNotContain("tabindex=\"-1\"", page.DocumentElement.OuterHtml);
        // The words of "Paper slips" are gone.
        var text = page.QuerySelector("main")!.TextContent;
        Assert.DoesNotContain("exchanges", text);
        Assert.DoesNotContain("slip", text.Replace(LongAbout, ""));
    }
}
