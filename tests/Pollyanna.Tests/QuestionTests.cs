using System.Net;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The anonymous question box (spec §6): the giver asks from the reveal card, the recipient
/// answers on <c>/events/{id}/questions</c>, unread badges and the home dot, closing, the
/// posting limit, and the rules on bodies, statuses and who may post.
/// </summary>
public sealed class QuestionTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!, ann = null!;
    Guid patId, annId, bob, carol;
    Guid ev;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        app.Clock.SetUtcNow(new DateTimeOffset(Tomorrow.At(10, 47, 12)));
        (pat, patId) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        bob = await app.PersonAsync("Bob");
        carol = await app.PersonAsync("Carol");
        // Pat gives to Ann; Carol is Pat's giver.
        ev = await app.EventAsync("Stockings", EventStatus.Open, [bob], [patId, annId, bob, carol]);
        await app.SealAsync(ev, Loop(patId, annId, bob, carol));
    }

    public async Task DisposeAsync()
    {
        pat.Dispose();
        ann.Dispose();
        await app.DisposeAsync();
    }

    string Page => $"/events/{ev}";
    string Questions => $"/events/{ev}/questions";

    Task<HttpResponseMessage> AskAsync(string body, TestBrowser? as_ = null) =>
        (as_ ?? pat).SubmitOnAsync(Page, $"{Page}/ask", Form(("body", body)));

    Task<HttpResponseMessage> ReplyAsync(string body) =>
        ann.SubmitOnAsync(Questions, $"{Questions}/reply", Form(("body", body)));

    static async Task<string> NoticeAsync(TestBrowser browser, string path)
    {
        var page = await browser.GetPageAsync(path);
        return (page.QuerySelector(".notice-error") ?? page.QuerySelector(".notice"))!.TextContent;
    }

    /// <summary>The page's one notice, which must sit with the thread form (the redirect scrolls past the top).</summary>
    static async Task<string> ThreadNoticeAsync(TestBrowser browser, string path)
    {
        var page = await browser.GetPageAsync(path);
        var notice = Assert.Single(page.QuerySelectorAll(".notice"));
        Assert.True(notice.Closest("#ask") is not null || notice.NextElementSibling?.Id == "reply", notice.ParentElement!.OuterHtml);
        return notice.TextContent;
    }

    static async Task<List<string>> HomeLinesAsync(TestBrowser browser) =>
        [.. (await browser.GetPageAsync("/")).QuerySelectorAll(".slip-update").Select(e => e.TextContent.Trim())];

    [Fact]
    public async Task The_giver_asks_and_the_recipient_sees_it_without_their_name()
    {
        var response = await AskAsync("What's your shoe size?");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{Page}#ask", response.Headers.Location!.OriginalString);
        var page = await pat.GetPageAsync(Page);
        Assert.Contains("Sent", page.QuerySelector(".notice")!.TextContent);
        // The card comes back unfolded at the thread, showing the question as Pat's own note.
        Assert.True(page.QuerySelector("#slip")!.HasAttribute("open"));
        var mine = page.QuerySelector("#ask .passed-mine")!;
        Assert.Contains("You asked", mine.TextContent);
        Assert.Contains("What's your shoe size?", mine.TextContent);

        // Ann: a dot on the home page and a note pinned on the event page, naming nobody.
        Assert.Equal(["Your giver asked you something"], await HomeLinesAsync(ann));
        var link = (await ann.GetPageAsync(Page)).QuerySelector(".passed-link")!;
        Assert.Equal(Questions, link.GetAttribute("href"));
        Assert.Contains("Your giver asked you something", link.TextContent);
        Assert.Contains("1 NEW", link.TextContent);

        var questions = await ann.GetPageAsync(Questions);
        var note = questions.QuerySelector(".passed-theirs")!;
        Assert.StartsWith("Giver ·", note.QuerySelector(".passed-label")!.TextContent);
        Assert.Contains("What's your shoe size?", note.TextContent);
        Assert.NotNull(note.QuerySelector(".passed-new"));
        Assert.DoesNotContain("Pat", questions.Body!.TextContent);

        // Opening the page read it.
        Assert.Null((await ann.GetPageAsync(Questions)).QuerySelector(".passed-new"));
        Assert.Empty(await HomeLinesAsync(ann));
        Assert.Contains("Questions from your giver", (await ann.GetPageAsync(Page)).QuerySelector(".passed-link")!.TextContent);
    }

    [Fact]
    public async Task The_recipient_replies_and_the_giver_sees_it_inside_the_card()
    {
        await AskAsync("Dark or milk chocolate?");
        await ReplyAsync("Dark, the darker the better!");
        Assert.Contains("Reply sent", await ThreadNoticeAsync(ann, Questions));
        var mine = (await ann.GetPageAsync(Questions)).QuerySelectorAll(".passed-note").Last();
        Assert.Contains("passed-mine", mine.ClassName);
        Assert.Contains("You replied", mine.TextContent);

        // Pat: a dot, a hint on the folded card, and the reply inside it.
        Assert.Equal(["Your recipient replied"], await HomeLinesAsync(pat));
        var page = await pat.GetPageAsync(Page);
        Assert.Contains("A NEW REPLY INSIDE", page.QuerySelector(".reveal-cover")!.TextContent);
        var reply = page.QuerySelector("#ask .passed-theirs")!;
        Assert.Contains("Ann replied", reply.TextContent);
        Assert.Contains("Dark, the darker the better!", reply.TextContent);
        Assert.NotNull(reply.QuerySelector(".passed-new"));

        // Unfolding the card reads the thread up to what it showed.
        await pat.SubmitAsync(page, $"{Page}/seen");
        Assert.Empty(await HomeLinesAsync(pat));
        page = await pat.GetPageAsync(Page);
        Assert.DoesNotContain("A NEW REPLY INSIDE", page.QuerySelector(".reveal-cover")!.TextContent);
        Assert.Null(page.QuerySelector("#ask .passed-new"));
    }

    [Fact]
    public async Task A_reply_that_arrives_after_the_card_was_drawn_stays_unread()
    {
        await AskAsync("Gloves or a scarf?");
        await ReplyAsync("Gloves.");
        var page = await pat.GetPageAsync(Page);
        await ReplyAsync("Actually, a scarf.");

        await pat.SubmitAsync(page, $"{Page}/seen");
        Assert.Equal(["Your recipient replied"], await HomeLinesAsync(pat));
        var notes = (await pat.GetPageAsync(Page)).QuerySelectorAll("#ask .passed-note").ToList();
        Assert.Equal(3, notes.Count);
        Assert.Null(notes[1].QuerySelector(".passed-new"));
        Assert.NotNull(notes[2].QuerySelector(".passed-new"));
    }

    [Fact]
    public async Task Only_the_giver_starts_a_thread()
    {
        var questions = await ann.GetPageAsync(Questions);
        Assert.Contains("No questions yet", questions.Body!.TextContent);
        Assert.Empty(questions.QuerySelectorAll("textarea"));
        Assert.Null((await ann.GetPageAsync(Page)).QuerySelector(".passed-link"));

        await ann.PostWithTokenAsync(Questions, $"{Questions}/reply", Form(("body", "Hello!")));
        Assert.Contains("Your giver writes first", await NoticeAsync(ann, Questions));
        Assert.Equal(0, await app.WithDbAsync(db => db.Messages.CountAsync()));

        // Pat sees an empty thread with a box for the first question.
        var page = await pat.GetPageAsync(Page);
        Assert.Empty(page.QuerySelectorAll("#ask .passed-note"));
        Assert.Equal("Your question", page.QuerySelector("label[for='ask-body']")!.TextContent);
    }

    [Fact]
    public async Task Closing_stops_both_sides_until_the_recipient_reopens()
    {
        await AskAsync("Favourite colour?");
        await ann.SubmitOnAsync(Questions, $"{Questions}/close");
        Assert.Contains("Conversation closed", await NoticeAsync(ann, Questions));

        var page = await pat.GetPageAsync(Page);
        Assert.Contains("Your recipient has closed this conversation.", page.QuerySelector("#ask")!.TextContent);
        Assert.Null(page.QuerySelector($"form[action='{Page}/ask']"));
        await pat.PostWithTokenAsync(Page, $"{Page}/ask", Form(("body", "Please?")));
        Assert.Contains("Your recipient has closed this conversation.", await ThreadNoticeAsync(pat, Page));

        var questions = await ann.GetPageAsync(Questions);
        Assert.Contains("You closed this conversation", questions.Body!.TextContent);
        Assert.Null(questions.QuerySelector($"form[action='{Questions}/reply']"));
        await ann.PostWithTokenAsync(Questions, $"{Questions}/reply", Form(("body", "Blue.")));
        Assert.Contains("Reopen it to write again", await ThreadNoticeAsync(ann, Questions));
        Assert.Single(await app.ThreadOfAsync(ev, patId));

        await ann.SubmitOnAsync(Questions, $"{Questions}/reopen");
        await AskAsync("Please?");
        await ReplyAsync("Blue.");
        Assert.Equal(["Favourite colour?", "Please?", "Blue."], (await app.ThreadOfAsync(ev, patId)).Select(m => m.Body));
    }

    [Fact]
    public async Task Bodies_are_checked_normalized_and_shown_escaped()
    {
        await AskAsync("   ");
        Assert.Contains("Write something first", await ThreadNoticeAsync(pat, Page));
        await pat.PostWithTokenAsync(Page, $"{Page}/ask", Form(("body", new string('x', EventManager.MessageMax + 1))));
        Assert.Contains("Keep it under 1,000 characters", await ThreadNoticeAsync(pat, Page));
        Assert.Empty(await app.ThreadOfAsync(ev, patId));

        await pat.PostWithTokenAsync(Page, $"{Page}/ask", Form(("body", new string('x', EventManager.MessageMax))));
        await AskAsync("  <b>Socks</b>\r\nor gloves?  ");
        var thread = await app.ThreadOfAsync(ev, patId);
        Assert.Equal(2, thread.Count);
        Assert.Equal("<b>Socks</b>\nor gloves?", thread[1].Body);

        var html = await (await ann.GetAsync(Questions)).Content.ReadAsStringAsync();
        Assert.Contains("&lt;b&gt;Socks&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Socks</b>", html);
    }

    [Fact]
    public async Task Messages_are_ordered_by_the_thread_counter_and_stored_on_the_hour()
    {
        await AskAsync("one");
        await ReplyAsync("two");
        await AskAsync("three");
        app.Clock.Advance(TimeSpan.FromMinutes(20)); // 11:07: the next hour
        await ReplyAsync("four");

        var thread = await app.ThreadOfAsync(ev, patId);
        Assert.Equal([1, 2, 3, 4], thread.Select(m => m.Seq));
        Assert.Equal([MessageSender.Giver, MessageSender.Recipient, MessageSender.Giver, MessageSender.Recipient], thread.Select(m => m.Sender));
        Assert.All(thread.Take(3), m => Assert.Equal(Tomorrow.At(10), m.CreatedAt));
        Assert.Equal(Tomorrow.At(11), thread[3].CreatedAt);

        var notes = (await ann.GetPageAsync(Questions)).QuerySelectorAll(".passed-note").ToList();
        Assert.Equal(["one", "two", "three", "four"], notes.Select(n => n.QuerySelector(".passed-body")!.TextContent));
        // The script turns the hour into e.g. "MON, ABOUT 10 AM"; the page itself carries only the hour.
        Assert.Equal(Tomorrow.At(10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), notes[0].QuerySelector("time[data-hour]")!.GetAttribute("datetime"));
        Assert.Equal(Tomorrow.At(10).ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture), notes[0].QuerySelector("time")!.TextContent);

        // Another thread counts from 1 on its own.
        await app.MessageAsync(ev, carol, MessageSender.Giver, "Hat size?");
        Assert.Equal(1, (await app.ThreadOfAsync(ev, carol)).Single().Seq);
    }

    [Fact]
    public async Task Posting_is_limited_per_person_across_their_sessions()
    {
        app.Services.GetRequiredService<PostingLimit>().PerHour = 3;
        await AskAsync("one");
        await AskAsync("two");
        // Signing in again doesn't reset the count.
        var (again, _) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _a = again;
        await AskAsync("three", again);
        await AskAsync("four", again);
        Assert.Contains("You've sent a lot of messages this hour", await ThreadNoticeAsync(again, Page));
        await AskAsync("five");
        Assert.Contains("You've sent a lot of messages this hour", await ThreadNoticeAsync(pat, Page));
        Assert.Equal(3, (await app.ThreadOfAsync(ev, patId)).Count);

        // Someone else has their own allowance, and an hour later Pat has theirs back.
        await ReplyAsync("Ann's reply");
        Assert.Contains("Reply sent", await ThreadNoticeAsync(ann, Questions));
        app.Clock.Advance(TimeSpan.FromMinutes(59));
        await AskAsync("still too soon");
        Assert.Contains("You've sent a lot of messages this hour", await ThreadNoticeAsync(pat, Page));
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await AskAsync("an hour later");
        Assert.Equal(5, (await app.ThreadOfAsync(ev, patId)).Count);
    }

    [Fact]
    public async Task The_box_is_only_for_drawn_events_and_their_participants()
    {
        // An outsider and an organizer who doesn't take part get "not found" everywhere.
        var (outsider, _) = await app.SignInAsync(new MockIdentity("olly", "Olly", "olly@example.com", true, null));
        using var _o = outsider;
        var (organizer, orgId) = await app.SignInAsync(new MockIdentity("org", "Org", "org@example.com", true, null));
        using var _g = organizer;
        await app.WithDbAsync(async db => { db.Organizers.Add(new Organizer { EventId = ev, PersonId = orgId }); await db.SaveChangesAsync(); });
        foreach (var browser in new[] { outsider, organizer })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(Questions)).StatusCode);
            foreach (var action in new[] { $"{Questions}/reply", $"{Questions}/close", $"{Questions}/reopen" })
                Assert.Equal(HttpStatusCode.NotFound, (await browser.PostWithTokenAsync("/", action, Form(("body", "hi")))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostWithTokenAsync("/", $"{Page}/ask", Form(("body", "hi")))).StatusCode);
        // The organizer can see the event, so asking is refused like their other event-page posts.
        await organizer.PostWithTokenAsync("/", $"{Page}/ask", Form(("body", "hi")));
        Assert.Contains("You aren't in this event", await NoticeAsync(organizer, $"{Page}/manage"));
        Assert.Equal(0, await app.WithDbAsync(db => db.Messages.CountAsync()));
        Assert.False((await app.AssignmentOfAsync(ev, patId)).ThreadClosed);

        // Before the draw, the questions page goes back to the event, and posting is refused.
        var open = await app.EventAsync("Next year", EventStatus.Open, [bob], [patId, annId, bob]);
        var response = await ann.GetAsync($"/events/{open}/questions");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/events/{open}", response.Headers.Location!.OriginalString);
        await pat.PostWithTokenAsync($"/events/{open}", $"/events/{open}/ask", Form(("body", "hi")));
        Assert.Contains("Names haven't been drawn", await NoticeAsync(pat, $"/events/{open}"));
        Assert.Equal(0, await app.WithDbAsync(db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task Pages_use_no_inline_styles_or_scripts_and_threads_never_expose_ids()
    {
        await AskAsync("Shoe size?");
        await ReplyAsync("Nine.");
        await ann.SubmitOnAsync(Questions, $"{Questions}/close");
        await ann.SubmitOnAsync(Questions, $"{Questions}/reopen");
        await pat.SubmitOnAsync(Page, $"{Page}/seen");

        var rows = await app.WithDbAsync(db => db.Assignments.Where(a => a.EventId == ev).Select(a => a.Id.ToString()).ToListAsync());
        var messages = await app.WithDbAsync(db => db.Messages.Select(m => m.Id.ToString()).ToListAsync());
        Assert.Equal(2, messages.Count);
        var secrets = rows.Concat(messages).ToList();

        foreach (var (browser, path) in new[] { (pat, Page), (ann, Page), (ann, Questions) })
        {
            var html = await (await browser.GetAsync(path)).Content.ReadAsStringAsync();
            var page = await browser.GetPageAsync(path);
            Assert.Empty(page.QuerySelectorAll("[style], style"));
            Assert.All(page.QuerySelectorAll("script"), s => Assert.Equal("", s.TextContent));
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, html, StringComparison.OrdinalIgnoreCase);
        }

        var logs = app.Logs.Entries.Select(e => e.Message).ToList();
        foreach (var secret in secrets)
            Assert.DoesNotContain(logs, m => m.Contains(secret, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("POST /events/{id:guid}/questions/{handler?} 302", app.Logs.Entries.Where(e => e.Category == "Pollyanna.Access").Select(e => e.Message));
    }
}
