using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>
/// The event page before the draw and the leave confirmation in the "Tablecloth" identity (spec §7
/// Page head, Event page before the draw, Confirmations; §12 Statuses).
/// </summary>
public sealed class EventPageTests : IAsyncLifetime
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

    static readonly string[] Palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];

    static string Text(IElement? e) => Regex.Replace(e?.TextContent ?? "", @"\s+", " ").Trim();

    [Fact]
    public async Task Before_the_draw_the_notes_editor_is_the_panel_on_the_cloth_under_the_head()
    {
        var org = await app.PersonAsync("Org");
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [org], [me, carol], budget: "$25");

        var page = await browser.GetPageAsync($"/events/{ev}");

        // Head: a "Home" back link, the title, the countdown on its own, then one small line
        // with the date, the budget and the status in plain words.
        var head = page.QuerySelector(".page-head")!;
        var back = head.QuerySelector("a.page-head-back")!;
        Assert.Equal(("Home", "/"), (Text(back), back.GetAttribute("href")));
        Assert.Equal("Stockings 2026", Text(head.QuerySelector("h1.page-title")));
        Assert.NotNull(head.QuerySelector(".page-countdown[data-countdown='2026-12-24']"));
        Assert.Equal("Dec 24, 2026 · $25 · names not drawn yet", Text(head.QuerySelector(".page-head-line")));
        Assert.Null(head.QuerySelector("nav"));
        Assert.Empty(page.QuerySelectorAll(".stamp, #status-heading"));

        // The one panel: the line saying what happens next, then the labeled notes editor.
        var panel = Assert.Single(page.QuerySelectorAll(".cloth > .panel"));
        Assert.Equal("Write your notes so your giver knows what you'd like. You'll see who you're giving to once names are drawn.",
            Text(panel.QuerySelector("p")));
        Assert.Equal("Likes, sizes, no-gos. Everyone taking part can read them.", Text(panel.QuerySelector("label[for='notes']")));
        Assert.NotNull(panel.QuerySelector("form textarea#notes[name='notes']"));
        var save = Assert.Single(panel.QuerySelectorAll(".button"));
        Assert.Equal(("Save notes", true), (Text(save), save.ClassList.Contains("button-fill")));
        Assert.Single(page.QuerySelectorAll(".button-fill"));

        // Under the cloth: the people taking part, then "Leave this event" as the very last thing.
        var below = page.QuerySelector(".cloth + .below")!;
        Assert.Equal("Taking part (2)", Text(below.QuerySelector("h2")));
        Assert.Contains("Carol", Text(below.QuerySelector(".people")));
        var last = page.QuerySelectorAll("main a, main button").Last();
        Assert.Equal(("Leave this event", $"/events/{ev}/leave"), (Text(last), last.GetAttribute("href")));
        Assert.Contains("small", last.ParentElement!.ClassList);
    }

    [Fact]
    public async Task After_a_redraw_the_line_says_names_are_being_drawn_again()
    {
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me]);
        await app.WithDbAsync(async db => { (await db.Events.SingleAsync(e => e.Id == ev)).Redrawn = true; await db.SaveChangesAsync(); });

        var page = await browser.GetPageAsync($"/events/{ev}");

        Assert.Equal("Names are being drawn again. You'll see who you're giving to once they are.", Text(page.QuerySelector(".cloth .panel p")));
    }

    [Fact]
    public async Task An_organizer_who_takes_part_gets_the_two_way_switch_instead_of_the_back_link()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        var page = await browser.GetPageAsync($"/events/{ev}");

        var head = page.QuerySelector(".page-head")!;
        Assert.Null(head.QuerySelector(".page-head-back"));
        var links = head.QuerySelectorAll("nav.switch a").Select(a => (Text(a), a.GetAttribute("href"), a.GetAttribute("aria-current"))).ToList();
        Assert.Equal([("My page", $"/events/{ev}", "page"), ("Organizing", $"/events/{ev}/manage", null)], links);
        // The event's name comes first; the switch sits right under it.
        Assert.Equal(["h1", "nav"], head.Children.Take(2).Select(c => c.LocalName));
        Assert.Equal("Dec 24, 2026 · $25 · draft, only organizers can see it", Text(head.QuerySelector(".page-head-line")));
    }

    [Fact]
    public async Task The_leave_confirmation_asks_its_question_as_the_title_with_no_cloth()
    {
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me]);

        var page = await browser.GetPageAsync($"/events/{ev}/leave");

        var back = page.QuerySelector(".page-head-back")!;
        Assert.Equal(("Stockings", $"/events/{ev}"), (Text(back), back.GetAttribute("href")));
        Assert.Equal("Leave this event?", Text(page.QuerySelector("h1")));
        Assert.Contains("Your notes for this event are deleted.", Text(page.QuerySelector("main")));
        Assert.DoesNotContain("someone new", Text(page.QuerySelector("main")));
        Assert.Empty(page.QuerySelectorAll(".cloth"));
        var button = Assert.Single(page.QuerySelectorAll("main form button"));
        Assert.Equal(("Leave this event", true), (Text(button), button.ClassList.Contains("button-fill")));
        Assert.Equal($"/events/{ev}", page.QuerySelectorAll("main a").Single(a => Text(a) == "Stay in").GetAttribute("href"));
    }

    [Fact]
    public async Task Once_drawn_the_second_confirmation_says_one_other_person_gives_to_someone_new()
    {
        var org = await app.PersonAsync("Org");
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me, ann, bob]);
        await app.SealAsync(ev, Seed.Loop(me, ann, bob));

        var page = await TestBrowser.ParseAsync(await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave"));

        Assert.Equal("Leave this event?", Text(page.QuerySelector("h1")));
        Assert.Contains("Names are drawn, so one other person will be giving to someone new.", Text(page.QuerySelector("main")));
        Assert.Empty(page.QuerySelectorAll(".cloth"));
        Assert.Equal("Leave this event", Text(Assert.Single(page.QuerySelectorAll("main form button.button-fill"))));
        Assert.Contains(page.QuerySelectorAll("main a"), a => Text(a) == "Stay in");
    }

    [Fact]
    public async Task The_moved_pages_declare_light_only_and_use_only_the_six_colors()
    {
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me, org], [me, org]);

        foreach (var path in new[] { $"/events/{ev}", $"/events/{ev}/leave" })
        {
            var page = await browser.GetPageAsync(path);

            Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
            Assert.Empty(page.QuerySelectorAll("[style], style"));
            var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
            Assert.All(hexes, h => Assert.Contains(h, Palette));
        }
    }
}
