using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// "Questions for you" in the "Tablecloth" look (spec §7, §12): the thread reads as a
/// conversation, the giver on the left and the viewer on the right, oldest at the top with a
/// line between days; on the cloth with a filled "Send" while a question is unanswered, on
/// white with the reply box in a bar otherwise. The question box's rules are in QuestionTests.
/// </summary>
public sealed class QuestionConversationTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser ann = null!;
    Guid patId, annId;
    Guid ev;

    // The clock only moves forward: it starts before the first message and ends on "today",
    // Tuesday, December 3, 2030, mid-afternoon (UTC).
    static readonly DateTimeOffset Start = new(2030, 11, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = new(2030, 12, 3, 15, 30, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        app.Clock.SetUtcNow(Start);
        (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        patId = await app.PersonAsync("Pat");
        var bob = await app.PersonAsync("Bob");
        // Pat gives to Ann.
        ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [bob], [patId, annId, bob]);
        await app.SealAsync(ev, Loop(patId, annId, bob));
    }

    public async Task DisposeAsync()
    {
        ann.Dispose();
        await app.DisposeAsync();
    }

    string Questions => $"/events/{ev}/questions";

    Task SayAsync(MessageSender sender, string body, DateTimeOffset at)
    {
        app.Clock.SetUtcNow(at);
        return app.MessageAsync(ev, patId, sender, body);
    }

    /// <summary>Moves the clock to "today" and signs Ann in again there, since her first session is weeks old by then.</summary>
    async Task TodayAsync()
    {
        app.Clock.SetUtcNow(Now);
        ann.Dispose();
        (ann, _) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
    }

    static List<string> Lines(IElement conversation) =>
        [.. conversation.Children.Select(li => li.ClassList.Contains("day-line")
            ? $"-- {li.TextContent.Trim()} --"
            : $"{(li.ClassList.Contains("message-mine") ? "me" : "giver")}: {li.QuerySelector(".bubble-body")!.TextContent.Trim()}")];

    [Fact]
    public async Task The_page_head_names_the_page_and_says_the_giver_stays_secret()
    {
        var page = await ann.GetPageAsync(Questions);

        var head = page.QuerySelector(".page-head")!;
        Assert.Equal("Questions for you", head.QuerySelector("h1")!.TextContent);
        Assert.Equal("Your giver asks, you answer. Their name stays a secret.", head.QuerySelector(".page-head-line")!.TextContent);
        Assert.Equal($"/events/{ev}", head.QuerySelector(".page-head-back")!.GetAttribute("href"));
        Assert.Equal("Cousins' Swap", head.QuerySelector(".page-head-back")!.TextContent);
        Assert.StartsWith("Questions for you", page.Title);
    }

    [Fact]
    public async Task With_nothing_asked_the_page_says_so_with_no_cloth_and_no_reply_box()
    {
        var page = await ann.GetPageAsync(Questions);

        Assert.Contains("No questions yet. When your giver asks you something, it shows up here and you can reply.", page.QuerySelector("main")!.TextContent);
        Assert.Null(page.QuerySelector(".cloth"));
        Assert.Null(page.QuerySelector("form"));
    }

    [Fact]
    public async Task The_conversation_runs_oldest_first_with_a_line_between_days()
    {
        await SayAsync(MessageSender.Giver, "Do you have a table for jigsaw puzzles?", new(2030, 11, 20, 16, 2, 0, TimeSpan.Zero));
        await SayAsync(MessageSender.Recipient, "Yes, a small one that folds away.", new(2030, 12, 1, 8, 15, 0, TimeSpan.Zero));
        await SayAsync(MessageSender.Giver, "What's your favorite kind of tea?", new(2030, 12, 3, 9, 14, 0, TimeSpan.Zero));
        await SayAsync(MessageSender.Recipient, "Earl Grey,\nor anything smoky.", new(2030, 12, 3, 9, 40, 0, TimeSpan.Zero));
        await TodayAsync();

        var page = await ann.GetPageAsync(Questions);

        var conversation = page.QuerySelector("ol.conversation")!;
        Assert.Equal([
            "-- November 20 --", "giver: Do you have a table for jigsaw puzzles?",
            "-- Sunday --", "me: Yes, a small one that folds away.",
            "-- Today --", "giver: What's your favorite kind of tea?", "me: Earl Grey,\nor anything smoky.",
        ], Lines(conversation));
        // Each message carries its hour, rounded, in machine form for the script and in words.
        var times = conversation.QuerySelectorAll(".message time").ToList();
        Assert.Equal("2030-11-20T16:00:00Z", times[0].GetAttribute("datetime"));
        Assert.Contains("about 4 PM", times[0].TextContent);
        Assert.Contains("about 8 AM", times[1].TextContent);
        Assert.Contains("about 9 AM", times[3].TextContent);
        // A screen reader hears who said what.
        Assert.StartsWith("Your giver:", conversation.QuerySelector(".message-theirs .bubble")!.TextContent.Trim());
        Assert.StartsWith("You:", conversation.QuerySelector(".message-mine .bubble")!.TextContent.Trim());
    }

    [Fact]
    public async Task An_unanswered_question_puts_the_conversation_and_the_reply_box_on_the_cloth_marked_new()
    {
        await SayAsync(MessageSender.Giver, "Do you have a table for jigsaw puzzles?", new(2030, 12, 1, 16, 0, 0, TimeSpan.Zero));
        await SayAsync(MessageSender.Recipient, "Yes, a small one.", new(2030, 12, 2, 8, 0, 0, TimeSpan.Zero));
        await SayAsync(MessageSender.Giver, "What's your favorite kind of tea?", new(2030, 12, 3, 9, 0, 0, TimeSpan.Zero));
        await SayAsync(MessageSender.Giver, "Loose or bags?", new(2030, 12, 3, 9, 0, 0, TimeSpan.Zero));
        await TodayAsync();

        // Twice: opening the page reads the thread, but the question is new until it's answered.
        foreach (var _ in new[] { 1, 2 })
        {
            var page = await ann.GetPageAsync(Questions);

            var panel = Assert.Single(page.QuerySelectorAll(".cloth > .panel"));
            Assert.NotNull(panel.QuerySelector("ol.conversation"));
            var form = panel.QuerySelector("form#reply")!;
            var box = form.QuerySelector("textarea[name=body]")!;
            Assert.Equal("1", box.GetAttribute("rows"));
            Assert.Equal("Reply to your giver", box.GetAttribute("placeholder"));
            Assert.Equal("Reply to your giver", box.GetAttribute("aria-label"));
            var send = Assert.Single(form.QuerySelectorAll("button"));
            Assert.Equal("Send", send.TextContent.Trim());
            Assert.Contains("button-fill", send.ClassList);
            Assert.Single(page.QuerySelectorAll(".button-fill"));

            // Only the questions after the last reply are new.
            var messages = panel.QuerySelectorAll(".message").ToList();
            Assert.Equal([false, false, true, true], messages.Select(m => m.QuerySelector(".message-new") is not null));
            Assert.Equal("new", messages[3].QuerySelector(".message-new")!.TextContent);

            // "Close this conversation" is a small link under the reply box.
            var close = page.QuerySelector($"form[action='{Questions}/close'] button")!;
            Assert.Equal("Close this conversation", close.TextContent.Trim());
            Assert.Contains("link-button", close.ClassList);
            Assert.NotEqual((DocumentPositions)0, form.CompareDocumentPosition(close) & DocumentPositions.Following);
        }
    }

    [Fact]
    public async Task With_nothing_new_there_is_no_cloth_and_the_reply_box_sits_in_a_bar_with_send_outlined()
    {
        await SayAsync(MessageSender.Giver, "What's your favorite kind of tea?", new(2030, 12, 3, 9, 0, 0, TimeSpan.Zero));
        await SayAsync(MessageSender.Recipient, "Earl Grey.", new(2030, 12, 3, 10, 0, 0, TimeSpan.Zero));
        await TodayAsync();

        var page = await ann.GetPageAsync(Questions);

        Assert.Null(page.QuerySelector(".cloth"));
        Assert.Null(page.QuerySelector(".message-new"));
        Assert.Null(page.QuerySelector(".button-fill"));
        var conversation = page.QuerySelector("ol.conversation")!;
        var bar = page.QuerySelector(".reply-bar")!;
        Assert.NotEqual((DocumentPositions)0, conversation.CompareDocumentPosition(bar) & DocumentPositions.Following);
        var send = bar.QuerySelector("form#reply button")!;
        Assert.Equal("Send", send.TextContent.Trim());
        Assert.Contains("button", send.ClassList);
        Assert.NotNull(page.QuerySelector($"form[action='{Questions}/close'] .link-button"));
    }

    [Fact]
    public async Task A_closed_conversation_has_no_cloth_and_offers_to_reopen()
    {
        await SayAsync(MessageSender.Giver, "What's your favorite kind of tea?", new(2030, 12, 3, 9, 0, 0, TimeSpan.Zero));
        await TodayAsync();
        await ann.SubmitOnAsync(Questions, $"{Questions}/close");

        var page = await ann.GetPageAsync(Questions);

        Assert.Null(page.QuerySelector(".cloth"));
        Assert.Null(page.QuerySelector("form#reply"));
        Assert.Contains("You closed this conversation", page.QuerySelector("#reply")!.TextContent);
        Assert.Equal("Reopen it", page.QuerySelector($"form[action='{Questions}/reopen'] button")!.TextContent.Trim());
    }

    [Fact]
    public async Task The_page_is_tablecloth_light_only_and_uses_only_the_six_colors()
    {
        await SayAsync(MessageSender.Giver, "What's your favorite kind of tea?", new(2030, 12, 3, 9, 0, 0, TimeSpan.Zero));
        await TodayAsync();

        var page = await ann.GetPageAsync(Questions);

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
        Assert.Contains(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/tablecloth.css"));
        Assert.DoesNotContain(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/site.css"));
        Assert.Empty(page.QuerySelectorAll("[style], style"));
        string[] palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];
        var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
        Assert.All(hexes, h => Assert.Contains(h, palette));
    }
}
