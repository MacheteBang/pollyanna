using Pollyanna.Data;
using Pollyanna.Happenings;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// "What's new" for the five kinds tied to an assignment (spec §16, docs/adr/0008): worked out on
/// each request from the sealed row's markers, never stored; dismissing moves those markers.
/// Pat gives to Ann, Ann to Bob, Bob to Cy, Cy to Pat.
/// </summary>
public sealed class WhatsNewAssignmentTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!, ann = null!;
    Guid me, annId, bob, cy;
    Guid ev;

    string Page => $"/events/{ev}";
    string Questions => $"/events/{ev}/questions";

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (pat, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        bob = await app.PersonAsync("Bob", "bob@example.com");
        cy = await app.PersonAsync("Cy", "cy@example.com");
        ev = await app.EventAsync("Stockings", EventStatus.Drawn, [me], [me, annId, bob, cy]);
        await app.SealAsync(ev, Loop(me, annId, bob, cy));
    }

    public async Task DisposeAsync()
    {
        pat.Dispose();
        ann.Dispose();
        await app.DisposeAsync();
    }

    sealed record Row(string Line, string Href, string Meta);

    static async Task<IReadOnlyList<Row>> RowsAsync(TestBrowser browser)
    {
        var page = await browser.GetPageAsync("/whats-new");
        return [.. page.QuerySelectorAll(".whats-new-rows > li").Select(li => new Row(
            li.QuerySelector("a")!.TextContent.Trim(),
            li.QuerySelector("a")!.GetAttribute("href")!,
            li.QuerySelector(".whats-new-meta")!.TextContent.Trim()))];
    }

    static async Task<IReadOnlyList<string>> LinesAsync(TestBrowser browser) =>
        [.. (await RowsAsync(browser)).Select(r => r.Line)];

    Task AskAsync(string body = "Size?") => pat.PostWithTokenAsync(Page, $"{Page}/ask", Form(("body", body)));

    Task ReplyAsync(string body = "Medium.") => ann.PostWithTokenAsync(Questions, $"{Questions}/reply", Form(("body", body)));

    // ---------- Which rows, worked out ----------

    [Fact]
    public async Task A_question_tells_the_recipient_linking_to_their_questions_page()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 1, 19, 42, 0, TimeSpan.Zero));
        await AskAsync();

        var row = Assert.Single(await RowsAsync(ann));
        Assert.Equal(new Row("Your giver asked you something", Questions, "Stockings · Today, 7:42 PM UTC"), row);
        Assert.Empty(await RowsAsync(pat));
    }

    /// <summary>Pat opens his card, as the gift untying posts it, having seen the thread up to <paramref name="upto"/>.</summary>
    Task OpenCardAsync(int upto = 1000) => pat.PostWithTokenAsync("/account", $"{Page}/seen", Form(("upto", upto.ToString())));

    Task DismissAsync(TestBrowser browser, HappeningKind kind) =>
        browser.PostWithTokenAsync("/whats-new", "/whats-new/dismiss", Form(("event", ev.ToString()), ("kind", kind.ToString())));

    static async Task<string> HomeLineAsync(TestBrowser browser) =>
        (await browser.GetPageAsync("/")).QuerySelector(".panel .waiting")?.TextContent.Trim() ?? "";

    Task CloseAsync() => ann.PostWithTokenAsync(Questions, $"{Questions}/close");

    Task ReopenAsync() => ann.PostWithTokenAsync(Questions, $"{Questions}/reopen");

    Task AnnNotesAsync(string notes) => ann.PostWithTokenAsync(Page, $"{Page}/notes", Form(("notes", notes)));

    Task PatchOutAsync(Guid dropout) =>
        pat.PostWithTokenAsync($"{Page}/manage", $"{Page}/manage/removeperson", Form(("personId", dropout.ToString()), ("change", "patch")));

    [Fact]
    public async Task A_reply_tells_the_giver_linking_to_the_plain_event_page_until_they_open_the_card()
    {
        await AskAsync();
        await ann.GetPageAsync(Questions);
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 1, 8, 5, 0, TimeSpan.Zero));
        await ReplyAsync();

        var row = Assert.Single(await RowsAsync(pat));
        Assert.Equal(new Row("Your recipient replied", Page, "Stockings · Today, 8:05 AM UTC"), row);
        Assert.Empty(await RowsAsync(ann));

        // Seeing the event page alone (the gift tied) isn't reading the reply; opening the card is.
        await pat.GetPageAsync(Page);
        Assert.Single(await RowsAsync(pat));
        await OpenCardAsync();
        Assert.Empty(await RowsAsync(pat));
    }

    [Fact]
    public async Task Closing_or_reopening_tells_the_giver_once_with_how_it_stands_now_until_they_open_the_card()
    {
        await AskAsync();
        await CloseAsync();
        Assert.Equal(new Row("Your recipient closed the conversation", Page, "Stockings"), Assert.Single(await RowsAsync(pat)));

        await ReopenAsync();
        Assert.Equal(["Your recipient reopened the conversation"], await LinesAsync(pat));
        Assert.True((await app.AssignmentOfAsync(ev, me)).ThreadChangeUnseen);

        await OpenCardAsync();
        Assert.Empty(await RowsAsync(pat));
        Assert.False((await app.AssignmentOfAsync(ev, me)).ThreadChangeUnseen);
    }

    [Fact]
    public async Task Closing_an_already_closed_conversation_changes_nothing()
    {
        await AskAsync();
        await CloseAsync();
        await OpenCardAsync();

        await CloseAsync();

        Assert.Empty(await RowsAsync(pat));
    }

    [Fact]
    public async Task Updated_notes_tell_the_giver_once_they_have_looked()
    {
        await AnnNotesAsync("Socks");
        Assert.Empty(await RowsAsync(pat));

        await OpenCardAsync();
        await AnnNotesAsync("Wool socks");

        Assert.Equal(new Row("Your recipient updated their notes", Page, "Stockings"), Assert.Single(await RowsAsync(pat)));
        await OpenCardAsync();
        Assert.Empty(await RowsAsync(pat));
    }

    [Fact]
    public async Task A_patch_tells_the_giver_theyre_giving_to_someone_new_without_naming_anyone()
    {
        // Ann drops out: Pat, who gave to Ann, now gives to Bob.
        await PatchOutAsync(annId);

        Assert.Equal(new Row("You're giving to someone new", Page, "Stockings"), Assert.Single(await RowsAsync(pat)));
        var main = (await pat.GetPageAsync("/whats-new")).QuerySelector("main")!.TextContent;
        Assert.DoesNotContain("Bob", main);
        Assert.DoesNotContain("Ann", main);
    }

    [Fact]
    public async Task One_row_per_kind_per_event_with_no_counts()
    {
        await AskAsync("One?");
        await AskAsync("Two?");
        await AskAsync("Three?");

        Assert.Equal(["Your giver asked you something"], await LinesAsync(ann));
        var bell = (await ann.GetPageAsync("/")).QuerySelector(".masthead .bell")!;
        Assert.Equal("What's new, 1 notification", bell.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task The_bell_counts_the_worked_out_rows_too()
    {
        await AskAsync();
        await CloseAsync();
        await ReopenAsync();
        await ann.GetPageAsync(Questions);
        await ReplyAsync();

        var bell = (await pat.GetPageAsync("/")).QuerySelector(".masthead .bell")!;
        Assert.Equal("What's new, 2 notifications", bell.GetAttribute("aria-label"));
        Assert.True(bell.ClassList.Contains("bell-new"));
    }

    [Fact]
    public async Task Nothing_is_worked_out_once_the_event_is_revealed()
    {
        await AskAsync();
        await CloseAsync();
        await pat.PostWithTokenAsync($"{Page}/manage", $"{Page}/manage/reveal", Form(("confirmed", "true")));

        Assert.DoesNotContain("Your giver asked you something", await LinesAsync(ann));
        Assert.DoesNotContain("Your recipient closed the conversation", await LinesAsync(pat));
    }

    // ---------- Order ----------

    [Fact]
    public async Task The_kinds_with_no_time_come_first_then_everything_else_newest_first()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero));
        await AskAsync();
        await OpenCardAsync();
        await ann.GetPageAsync(Questions);
        await ReplyAsync();
        await CloseAsync();
        await AnnNotesAsync("Gloves");
        app.Clock.Advance(TimeSpan.FromHours(1));
        var other = await app.EventAsync("Book Club", EventStatus.Draft, [annId], [annId, me]);
        await ann.PostWithTokenAsync($"/events/{other}/manage", $"/events/{other}/manage/open");
        await app.UpdateAssignmentAsync(ev, me, a => a.RecipientChanged = true);

        Assert.Equal([
            "You're giving to someone new",
            "Your recipient updated their notes",
            "Your recipient closed the conversation",
            "It's open: write your notes",
            "Your recipient replied",
        ], await LinesAsync(pat));
    }

    [Fact]
    public async Task A_question_goes_by_its_newest_unread_message()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero));
        await app.MessageAsync(ev, me, MessageSender.Giver, "Older", at: new DateTime(2026, 12, 1, 7, 0, 0, DateTimeKind.Utc));
        var other = await app.EventAsync("Book Club", EventStatus.Draft, [me], [me, annId]);
        await pat.PostWithTokenAsync($"/events/{other}/manage", $"/events/{other}/manage/open");
        Assert.Equal(["It's open: write your notes", "Your giver asked you something"], await LinesAsync(ann));

        app.Clock.Advance(TimeSpan.FromMinutes(30));
        await AskAsync("Newer?");

        var rows = await RowsAsync(ann);
        Assert.Equal(["Your giver asked you something", "It's open: write your notes"], rows.Select(r => r.Line));
        Assert.Equal("Stockings · Today, 9:30 AM UTC", rows[0].Meta);
    }

    // ---------- Dismissing ----------

    [Fact]
    public async Task Dismissing_a_question_reads_that_thread_only_and_the_home_line_goes()
    {
        // Ann asks Bob, who replies; then Pat asks Ann.
        await ann.PostWithTokenAsync(Page, $"{Page}/ask", Form(("body", "For Bob")));
        await app.MessageAsync(ev, annId, MessageSender.Recipient, "Bob's answer");
        await AskAsync();
        Assert.Equal("Your giver asked you something", await HomeLineAsync(ann));

        await DismissAsync(ann, HappeningKind.Question);

        Assert.Equal(["Your recipient replied"], await LinesAsync(ann));
        Assert.NotEqual("Your giver asked you something", await HomeLineAsync(ann));
        Assert.Equal(0, (await app.AssignmentOfAsync(ev, annId)).GiverReadUpto);
        var pats = await app.AssignmentOfAsync(ev, me);
        Assert.Equal(1, pats.RecipientReadUpto);
        Assert.Equal(0, pats.GiverReadUpto);
    }

    [Fact]
    public async Task Dismissing_a_reply_marks_the_givers_thread_read()
    {
        await AskAsync();
        await ann.GetPageAsync(Questions);
        await ReplyAsync();

        await DismissAsync(pat, HappeningKind.Reply);

        Assert.Empty(await RowsAsync(pat));
        var row = await app.AssignmentOfAsync(ev, me);
        Assert.Equal(2, row.GiverReadUpto);
        Assert.Equal(1, row.RecipientReadUpto);
    }

    [Fact]
    public async Task Dismissing_reads_only_up_to_the_message_the_row_was_shown_for()
    {
        // Ann's panel shows the question; another arrives (live) before she taps "×" on that row.
        await AskAsync("Size?");
        var shown = await ann.GetPageAsync("/");
        await AskAsync("Colour?");
        static bool Question(AngleSharp.Html.Dom.IHtmlFormElement f) =>
            f.HasAttribute("data-bell-dismiss") && f.QuerySelector("input[name='kind']")?.GetAttribute("value") == "Question";

        // The row's own form, as the panel posts it.
        await ann.SubmitAsync(shown, "/whats-new/dismiss", match: Question);

        Assert.Equal(1, (await app.AssignmentOfAsync(ev, me)).RecipientReadUpto);
        Assert.Equal(["Your giver asked you something"], await LinesAsync(ann));
        Assert.Equal("Your giver asked you something", await HomeLineAsync(ann));
    }

    [Fact]
    public async Task Dismissing_closed_or_reopened_clears_the_flag()
    {
        await AskAsync();
        await CloseAsync();

        await DismissAsync(pat, HappeningKind.ThreadClosed);

        Assert.Empty(await RowsAsync(pat));
        Assert.False((await app.AssignmentOfAsync(ev, me)).ThreadChangeUnseen);
    }

    [Fact]
    public async Task Dismissing_notes_updated_or_patched_clears_the_home_line_too()
    {
        await OpenCardAsync();
        await AnnNotesAsync("Gloves");
        Assert.Equal("Your recipient updated their notes", await HomeLineAsync(pat));
        await DismissAsync(pat, HappeningKind.NotesEdited);
        Assert.Empty(await RowsAsync(pat));
        Assert.NotEqual("Your recipient updated their notes", await HomeLineAsync(pat));

        await PatchOutAsync(annId);
        Assert.Equal("You're giving to someone new", await HomeLineAsync(pat));
        await DismissAsync(pat, HappeningKind.Patched);
        Assert.Empty(await RowsAsync(pat));
        Assert.NotEqual("You're giving to someone new", await HomeLineAsync(pat));
    }

    [Fact]
    public async Task Dismissing_works_without_the_script_through_the_rows_own_form()
    {
        await AskAsync();
        var page = await ann.GetPageAsync("/whats-new");
        Assert.Equal("Dismiss: Your giver asked you something", page.QuerySelector(".whats-new-rows button")!.GetAttribute("aria-label"));

        await ann.SubmitAsync(page, "/whats-new/dismiss");

        Assert.Empty(await RowsAsync(ann));
    }

    [Fact]
    public async Task Dismissing_once_revealed_moves_nothing()
    {
        await AskAsync();
        await pat.PostWithTokenAsync($"{Page}/manage", $"{Page}/manage/reveal", Form(("confirmed", "true")));

        await DismissAsync(ann, HappeningKind.Question);

        Assert.Equal(0, (await app.AssignmentOfAsync(ev, me)).RecipientReadUpto);
    }

    // ---------- The flag and the sealed row ----------

    [Fact]
    public async Task No_stored_row_is_ever_one_of_the_five_kinds()
    {
        HappeningKind[] tied = [HappeningKind.Question, HappeningKind.Reply, HappeningKind.ThreadClosed,
            HappeningKind.ThreadReopened, HappeningKind.Patched, HappeningKind.NotesEdited];
        Assert.All(tied, k => Assert.Null(HappeningKinds.Of(k).Stored));

        await OpenCardAsync();
        await AskAsync();
        await ann.GetPageAsync(Questions);
        await ReplyAsync();
        await CloseAsync();
        await ReopenAsync();
        await AnnNotesAsync("Gloves");
        await PatchOutAsync(bob);

        Assert.NotEmpty(await RowsAsync(pat));
        Assert.DoesNotContain(await app.WithDbAsync(db => db.InAppNotifications.ToListAsync()), n => tied.Contains(n.Kind));
    }

    [Fact]
    public async Task A_patch_resets_the_flag_with_the_thread_it_clears()
    {
        await AskAsync();
        await CloseAsync();
        Assert.True((await app.AssignmentOfAsync(ev, me)).ThreadChangeUnseen);

        // Ann drops out: Pat's thread with her goes, and the flag with it.
        await PatchOutAsync(annId);

        Assert.False((await app.AssignmentOfAsync(ev, me)).ThreadChangeUnseen);
        Assert.Equal(["You're giving to someone new"], await LinesAsync(pat));
    }

    [Fact]
    public async Task A_redraw_starts_every_flag_afresh()
    {
        await AskAsync();
        await CloseAsync();

        await pat.PostWithTokenAsync($"{Page}/manage", $"{Page}/manage/redraw");
        await pat.PostWithTokenAsync($"{Page}/manage", $"{Page}/manage/draw");

        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        Assert.All(await app.WithDbAsync(db => db.Assignments.Where(a => a.EventId == ev).ToListAsync()), a => Assert.False(a.ThreadChangeUnseen));
    }
}
