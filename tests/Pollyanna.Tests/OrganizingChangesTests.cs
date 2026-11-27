using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The rest of the organizing page on "Tablecloth" (spec §7 Organizing an event, items 4 to 6 and
/// the notices): "Who can't draw whom" and "Details and organizers" as closed rows with a summary,
/// "Bigger changes" always open at the bottom, the pairing list in place of the people once
/// revealed, and notices in plain words.
/// </summary>
public sealed class OrganizingChangesTests : IAsyncLifetime
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

    static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    async Task<string> NoticeAsync(Guid ev) =>
        Squash((await browser.GetPageAsync(Manage(ev))).QuerySelector(".notice")!.TextContent);

    async Task<(Guid Ann, Guid Bob, Guid Cy, Guid Dee)> FourOthersAsync() =>
        (await app.PersonAsync("Ann"), await app.PersonAsync("Bob"), await app.PersonAsync("Cy"), await app.PersonAsync("Dee"));

    async Task<Guid> DrawnAsync(params Guid[] people)
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], people);
        await app.SealAsync(ev, Loop(people));
        return ev;
    }

    // ---------- Notices ----------

    [Fact]
    public async Task Drawing_names_again_says_the_draw_is_undone()
    {
        var (ann, bob, cy, _) = await FourOthersAsync();
        var ev = await DrawnAsync(ann, bob, cy);

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/redraw");

        Assert.Equal("The draw is undone. Draw again when you're ready.", await NoticeAsync(ev));
    }

    [Fact]
    public async Task A_dropout_patch_says_one_person_is_giving_to_someone_new()
    {
        var (ann, bob, cy, dee) = await FourOthersAsync();
        var ev = await DrawnAsync(ann, bob, cy, dee);

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/removeperson", Form(("personId", cy.ToString()), ("change", "Patch")));

        Assert.Equal("Took Cy out. One person is now giving to someone new, and they'll see a notice. Nobody else changed.",
            await NoticeAsync(ev));
    }

    [Fact]
    public async Task A_late_joiner_patch_says_one_person_is_giving_to_someone_new_to_make_room()
    {
        var (ann, bob, cy, dee) = await FourOthersAsync();
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, dee]);
        var ev = await DrawnAsync(ann, bob, cy);

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addperson", Form(("personId", dee.ToString()), ("change", "Patch"), ("householdId", "")));

        Assert.Equal("Added Dee. One person is now giving to someone new to make room, and they'll see a notice. Nobody else changed.",
            await NoticeAsync(ev));
    }

    [Fact]
    public async Task Adding_someone_already_in_says_they_are_already_taking_part()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob]);

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addpeople", Form(("email", "ann@example.com")));

        Assert.Equal("Ann is already taking part.", await NoticeAsync(ev));
    }

    // ---------- Bigger changes ----------

    /// <summary>"Bigger changes": each change's button (label, red), and its one small line.</summary>
    static List<(string Label, bool Red, string Line)> Changes(IDocument page) =>
        [.. page.QuerySelectorAll("#changes .change").Select(c => (
            Squash(c.QuerySelector("summary")!.TextContent),
            c.QuerySelector("summary")!.ClassList.Contains("button-red"),
            Squash(c.QuerySelector(".change-line")!.TextContent)))];

    const string Dropout = "Only that one person's recipient changes, and nobody, you included, is told whose.";
    const string LateJoiner = "One person gives to someone new to make room. Nobody else changes.";
    const string Redraw = "Every assignment is deleted and the event goes back to open. Everyone is told names were drawn again.";
    const string Archive = "Pairings that haven't been revealed stay hidden for good.";
    const string Cancel = "Everyone's assignments go, and any question-box messages are deleted. This can't be undone.";

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    public async Task Before_the_draw_bigger_changes_hold_cancel_alone(EventStatus status)
    {
        var (ann, bob, _, _) = await FourOthersAsync();
        var ev = await app.EventAsync("Stockings", status, [me], [ann, bob]);

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal("Bigger changes", Squash(page.QuerySelector("#changes h2")!.TextContent));
        Assert.Equal([("Cancel this event", true, Cancel)], Changes(page));
    }

    [Fact]
    public async Task Once_drawn_bigger_changes_are_outlined_buttons_with_a_line_each_and_only_cancel_is_red()
    {
        var (ann, bob, cy, _) = await FourOthersAsync();
        var ev = await DrawnAsync(ann, bob, cy);

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal([
            ("Someone is dropping out", false, Dropout),
            ("Someone is joining late", false, LateJoiner),
            ("Draw names again", false, Redraw),
            ("Archive this event", false, Archive),
            ("Cancel this event", true, Cancel),
        ], Changes(page));
        // Outlined, every one: the filled button is the next step's.
        Assert.All(page.QuerySelectorAll("#changes summary"), s => Assert.Contains("button", s.ClassList));
        Assert.Empty(page.QuerySelectorAll("#changes .button-fill"));
        // "Bigger changes" is the last thing on the page, always open: no fold around it.
        Assert.Equal("changes", page.QuerySelector("main")!.QuerySelectorAll("section").Last().Id);
        Assert.Null(page.QuerySelector("#changes")!.Closest("details"));
    }

    [Fact]
    public async Task While_someone_waits_to_leave_drawing_again_is_the_next_step_not_a_bigger_change()
    {
        var (ann, bob, cy, _) = await FourOthersAsync();
        var ev = await DrawnAsync(ann, bob, cy);
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == ann)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true)));

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal(["Someone is dropping out", "Someone is joining late", "Archive this event", "Cancel this event"],
            Changes(page).Select(c => c.Label));
        Assert.Null(page.QuerySelector("#reveal"));
        Assert.Contains("Ann asked to leave", page.QuerySelector("#archive")!.TextContent);
    }

    [Fact]
    public async Task Once_revealed_bigger_changes_hold_cancel_and_say_nothing_else_changes()
    {
        var (ann, bob, cy, _) = await FourOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Revealed, [me], [ann, bob, cy]);

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal([("Cancel this event", true, Cancel)], Changes(page));
        Assert.Contains("Nothing to change now that everyone's been revealed.", Squash(page.QuerySelector("#changes")!.TextContent));
    }

    [Fact]
    public async Task An_archived_event_has_no_bigger_changes()
    {
        var (ann, bob, cy, _) = await FourOthersAsync();
        var ev = await app.EventAsync("Stockings", EventStatus.Archived, [me], [ann, bob, cy]);

        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector("#changes"));
    }

    [Fact]
    public async Task The_redraw_confirmation_says_it_in_full_and_draws_again()
    {
        var (ann, bob, cy, _) = await FourOthersAsync();
        var ev = await DrawnAsync(ann, bob, cy);

        var page = await browser.GetPageAsync(Manage(ev));

        var redraw = page.QuerySelector("#redraw")!;
        Assert.Contains("Every assignment is deleted and the event goes back to open, so people and rules can change before you draw again. Everyone is told names were drawn again.",
            Squash(redraw.QuerySelector("details")!.TextContent));
        Assert.NotNull(redraw.QuerySelector($"details form[action='{Manage(ev)}/redraw'] button[type=submit]"));

        await browser.SubmitAsync(page, $"{Manage(ev)}/redraw", match: f => f.Closest("#redraw") is not null);
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task The_dropout_and_late_joiner_confirmations_still_patch()
    {
        var (ann, bob, cy, dee) = await FourOthersAsync();
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, dee]);
        var erin = await app.PersonAsync("Erin");
        var ev = await DrawnAsync(ann, bob, cy, erin);

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.NotNull(page.QuerySelector($"#dropout details form[action='{Manage(ev)}/removeperson'] select[name=personId]"));
        await browser.SubmitAsync(page, $"{Manage(ev)}/removeperson", Form(("personId", cy.ToString()), ("change", "Patch")),
            f => f.Closest("#dropout") is not null);
        Assert.DoesNotContain(cy, await app.ParticipantsAsync(ev));

        page = await browser.GetPageAsync(Manage(ev));
        Assert.NotNull(page.QuerySelector($"#late details form[action='{Manage(ev)}/addperson'] input[name=personId][value='{dee}']"));
        await browser.SubmitAsync(page, $"{Manage(ev)}/addperson", Form(("personId", dee.ToString()), ("change", "Patch"), ("householdId", "")),
            f => f.Closest("#late") is not null);
        Assert.Contains(dee, await app.ParticipantsAsync(ev));
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task The_cancel_confirmation_says_what_goes_without_slips()
    {
        var (ann, bob, cy, _) = await FourOthersAsync();
        var ev = await DrawnAsync(ann, bob, cy);

        var text = Squash((await browser.GetPageAsync(Manage(ev))).QuerySelector("#cancel details")!.TextContent);

        Assert.Contains("Everyone's assignments go, and any question-box messages are deleted.", text);
        Assert.DoesNotMatch(@"\bslips?\b", text);
    }
}
