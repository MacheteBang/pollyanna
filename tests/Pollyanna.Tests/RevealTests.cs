using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>
/// The event page after the draw in the "Tablecloth" identity (spec §7 Event page, after the draw;
/// §12 The reveal; §5.2 the viewed flag): the gift tied with the bow, what sits around it, and how
/// it unties with and without the script.
/// </summary>
public sealed class RevealTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob, org;
    Guid ev;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("sam", "Sam", "sam@example.com", true, null));
        org = await app.PersonAsync("Org");
        ann = await app.PersonAsync("Ann");
        bob = await app.PersonAsync("Bob");
        ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me, ann, bob]);
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == ann)).Notes = "Lemon anything";
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == me)).Notes = "Warm socks, size 9. No candles please.";
            await db.SaveChangesAsync();
        });
        await app.SealAsync(ev, Seed.Loop(me, ann, bob));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    string Page => $"/events/{ev}";

    static string Text(IElement? e) => Regex.Replace(e?.TextContent ?? "", @"\s+", " ").Trim();

    [Fact]
    public async Task Tied_the_whole_band_is_the_gift_with_ribbons_and_the_bow_and_one_filled_button_below()
    {
        var page = await browser.GetPageAsync(Page);

        Assert.Null(page.QuerySelector("link[href^='/css/site.css']"));
        Assert.NotNull(page.QuerySelector("link[href^='/css/tablecloth.css']"));
        var gift = page.QuerySelector("#gift")!;
        Assert.False(gift.ClassList.Contains("untied"));
        var cloth = gift.QuerySelector(".cloth")!;
        Assert.NotNull(cloth.QuerySelector(".gift-ribbon-across"));
        Assert.NotNull(cloth.QuerySelector(".gift-ribbon-down"));
        Assert.NotNull(cloth.QuerySelector("svg.gift-bow"));
        // Tapping the gift unties it too.
        Assert.NotNull(cloth.QuerySelector($"a.gift-cover[href='{Page}/untie#gift']"));
        var button = gift.QuerySelector(".gift-under a.button")!;
        Assert.Equal(("Untie the bow", $"{Page}/untie#gift"), (Text(button), button.GetAttribute("href")));
        Assert.True(button.ClassList.Contains("button-fill"));
        Assert.Single(page.QuerySelectorAll("main .button-fill"));
        // The name is under the cloth for the script to show, under "You're giving to".
        Assert.Equal("You're giving to", Text(gift.QuerySelector(".gift-to")));
        Assert.Equal("Ann", Text(gift.QuerySelector(".revealed-name")));
    }

    [Fact]
    public async Task Tied_and_untied_the_page_is_light_only_with_no_inline_style_and_only_the_six_colors()
    {
        string[] palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];
        foreach (var path in new[] { Page, $"{Page}/untie" })
        {
            var page = await browser.GetPageAsync(path);

            Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
            Assert.Empty(page.QuerySelectorAll("[style], style"));
            var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
            Assert.All(hexes, h => Assert.Contains(h, palette));
            // Nothing says "slip" or "hat" any more (spec §7 Words).
            Assert.DoesNotMatch(@"\b(slip|hat)\b", Text(page.QuerySelector("main")).ToLowerInvariant());
        }
    }

    [Fact]
    public async Task The_first_untying_comes_from_the_viewed_flag_and_later_visits_are_shorter()
    {
        Assert.Equal("true", (await browser.GetPageAsync(Page)).QuerySelector("#gift")!.GetAttribute("data-first"));

        await browser.SubmitOnAsync(Page, $"{Page}/seen");

        Assert.Equal("false", (await browser.GetPageAsync(Page)).QuerySelector("#gift")!.GetAttribute("data-first"));
    }

    [Fact]
    public async Task Without_the_script_the_name_shows_untied_on_the_cloth_and_hiding_it_records_the_viewing()
    {
        var page = await browser.GetPageAsync($"{Page}/untie");

        var gift = page.QuerySelector("#gift")!;
        Assert.True(gift.ClassList.Contains("untied"));
        var panel = gift.QuerySelector(".cloth > .panel")!;
        Assert.Equal("Ann", Text(panel.QuerySelector(".revealed-name")));
        Assert.Contains("Lemon anything", Text(panel));
        Assert.NotNull(panel.QuerySelector($"form[action='{Page}/ask'] textarea"));
        Assert.NotNull(panel.QuerySelector($"form[action='{Page}/shopping'] input[type=checkbox][name=done]"));
        var hide = panel.QuerySelector($"form[action='{Page}/seen'] button")!;
        Assert.Equal("Hide the name", Text(hide));
        Assert.False((await app.AssignmentOfAsync(ev, me)).Viewed);

        var response = await browser.SubmitAsync(page, $"{Page}/seen");

        Assert.Equal($"{Page}#gift", response.Headers.Location!.OriginalString);
        Assert.True((await app.AssignmentOfAsync(ev, me)).Viewed);
        // Every visit starts tied.
        Assert.False((await browser.GetPageAsync(Page)).QuerySelector("#gift")!.ClassList.Contains("untied"));
    }

    /// <summary>The top-level blocks of the page body, in order, by what identifies them.</summary>
    static List<string> Order(AngleSharp.Html.Dom.IHtmlDocument page)
    {
        var marks = new (string Selector, string Name)[]
        {
            (".page-head", "head"), (".asked-link", "asked"), (".gift-again", "again"), ("#gift", "gift"),
            (".questions-link", "questions"), ("#your-notes", "notes"), ("#taking-part", "people"),
            ($"a[href$='/leave']", "leave"),
        };
        var all = page.QuerySelector("main")!.QuerySelectorAll("*").ToList();
        return [.. marks
            .Select(m => (m.Name, At: page.QuerySelector("main " + m.Selector) is { } e ? all.IndexOf(e) : -1))
            .Where(m => m.At >= 0).OrderBy(m => m.At).Select(m => m.Name)];
    }

    [Fact]
    public async Task An_unread_question_comes_above_everything_and_the_rest_follows_the_gift()
    {
        await app.MessageAsync(ev, bob, MessageSender.Giver, "What's your shoe size?");

        var page = await browser.GetPageAsync(Page);

        Assert.Equal(["head", "asked", "gift", "notes", "people", "leave"], Order(page));
        var asked = page.QuerySelector(".asked-link")!;
        Assert.Equal(("Your giver asked you something", $"{Page}/questions"), (Text(asked), asked.GetAttribute("href")));
    }

    [Fact]
    public async Task With_nothing_unread_questions_from_the_giver_are_a_quiet_link_under_the_gift()
    {
        await app.MessageAsync(ev, bob, MessageSender.Giver, "What's your shoe size?");
        await browser.GetPageAsync($"{Page}/questions");

        var page = await browser.GetPageAsync(Page);

        Assert.Equal(["head", "gift", "questions", "notes", "people", "leave"], Order(page));
        Assert.Equal("Questions from your giver", Text(page.QuerySelector(".questions-link")));
        Assert.Null(page.QuerySelector(".questions-link.button-fill"));
    }

    [Fact]
    public async Task My_notes_and_the_people_are_folded_to_a_preview_and_a_count()
    {
        var page = await browser.GetPageAsync(Page);

        var notes = page.QuerySelector("details#your-notes")!;
        Assert.False(notes.HasAttribute("open"));
        Assert.Equal("Your notes", Text(notes.QuerySelector("summary span")));
        Assert.Equal("Warm socks, size 9. No candles please.", Text(notes.QuerySelector("summary .fold-hint")));
        Assert.NotNull(notes.QuerySelector($"form[action='{Page}/notes'] textarea#notes"));
        var people = page.QuerySelector("details#taking-part")!;
        Assert.False(people.HasAttribute("open"));
        Assert.Equal(("Taking part", "3 people"), (Text(people.QuerySelector("summary span")), Text(people.QuerySelector("summary .fold-hint"))));
        Assert.Equal(["Ann", "Bob"], people.QuerySelectorAll(".person-name").Select(Text));
    }

    [Fact]
    public async Task After_a_redraw_the_notice_sits_above_the_gift_and_the_untying_is_a_first_time()
    {
        await app.WithDbAsync(async db => { (await db.Events.SingleAsync(e => e.Id == ev)).Redrawn = true; await db.SaveChangesAsync(); });

        var page = await browser.GetPageAsync(Page);

        Assert.Equal(["head", "again", "gift", "notes", "people", "leave"], Order(page));
        Assert.Equal("Names were drawn again, so this may be someone new.", Text(page.QuerySelector(".gift-again")));
        Assert.Equal("true", page.QuerySelector("#gift")!.GetAttribute("data-first"));
        await browser.SubmitOnAsync(Page, $"{Page}/seen");
        Assert.Null((await browser.GetPageAsync(Page)).QuerySelector(".gift-again"));
    }

    [Fact]
    public async Task After_a_patch_the_new_recipient_is_a_first_time_again()
    {
        await browser.SubmitOnAsync(Page, $"{Page}/seen");
        await app.UpdateAssignmentAsync(ev, me, a => a.RecipientChanged = true);

        var page = await browser.GetPageAsync(Page);

        Assert.Equal("true", page.QuerySelector("#gift")!.GetAttribute("data-first"));
        Assert.Contains("You're giving to someone new.", Text(page.QuerySelector(".gift-again")));
    }

    [Fact]
    public async Task Updated_notes_are_marked_on_the_panel()
    {
        await browser.SubmitOnAsync(Page, $"{Page}/seen");
        await app.WithDbAsync(async db =>
        {
            var p = await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == ann);
            p.Notes += " And tea.";
            p.NotesVersion++;
            await db.SaveChangesAsync();
        });

        var panel = (await browser.GetPageAsync(Page)).QuerySelector("#gift .panel")!;

        Assert.Equal("Notes updated since you last looked", Text(panel.QuerySelector("strong")));
        Assert.Contains("Lemon anything And tea.", Text(panel));
    }

    [Fact]
    public async Task Done_shopping_is_a_small_control_that_comes_back_untied()
    {
        var response = await browser.SubmitOnAsync(Page, $"{Page}/shopping", Seed.Form(("done", "true")));

        Assert.Equal($"{Page}#shopping", response.Headers.Location!.OriginalString);
        var page = await browser.GetPageAsync(Page);
        Assert.True(page.QuerySelector("#gift")!.ClassList.Contains("untied"));
        Assert.True(page.QuerySelector("#shopping input[name=done]")!.HasAttribute("checked"));
        Assert.Equal("Done shopping", Text(page.QuerySelector("#shopping label")));
        // The visit after that starts tied again.
        Assert.False((await browser.GetPageAsync(Page)).QuerySelector("#gift")!.ClassList.Contains("untied"));
    }

    [Fact]
    public async Task An_assignment_that_cant_be_read_says_to_ask_an_organizer_to_draw_names_again()
    {
        await app.WithDbAsync(async db =>
        {
            foreach (var a in await db.Assignments.Where(a => a.EventId == ev).ToListAsync())
                a.GiverTag = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync(Page);

        Assert.Null(page.QuerySelector("[data-gift]"));
        Assert.Empty(page.QuerySelectorAll(".cloth"));
        Assert.Equal("We can't show who you're giving to", Text(page.QuerySelector("#gift h2")));
        Assert.Equal("Names have been drawn, but yours can't be read any more. Ask an organizer to draw names again.", Text(page.QuerySelector("#gift p")));
    }
}
