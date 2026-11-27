using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Pollyanna.Data;
using Pollyanna.Happenings;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// What passes between a giver and a recipient arrives as content inside that one person's live
/// connection (spec §16 "Who a live signal goes to"): the pages it touches, as they would see
/// them, so their open page patches itself with no follow-up request to pair them in a log.
/// Gus gives to Rae, Rae to Bea, Bea to Gus; Olga organizes.
/// </summary>
public sealed class LiveContentTests : IAsyncLifetime
{
    TestApp app = null!;
    readonly Dictionary<string, (TestBrowser Browser, Guid Id)> cast = [];
    readonly Dictionary<string, LiveReader> tabs = [];
    Guid ev;

    static readonly string[] Names = ["Olga", "Gus", "Rae", "Bea"];

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        foreach (var name in Names)
            cast[name] = await app.SignInAsync(new MockIdentity(name.ToLowerInvariant(), name, $"{name.ToLowerInvariant()}@example.com", true, null));
        ev = await app.EventAsync("Stockings", EventStatus.Open, [Id("Olga")], [Id("Gus"), Id("Rae"), Id("Bea")]);
        await app.SealAsync(ev, Loop(Id("Gus"), Id("Rae"), Id("Bea")));
        foreach (var name in Names)
            tabs[name] = await Browser(name).OpenLiveAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var tab in tabs.Values)
            tab.Dispose();
        foreach (var (browser, _) in cast.Values)
            browser.Dispose();
        await app.DisposeAsync();
    }

    Guid Id(string name) => cast[name].Id;
    TestBrowser Browser(string name) => cast[name].Browser;
    string Page => $"/events/{ev}";

    async Task SettledAsync()
    {
        await app.Live.SettleAsync();
        foreach (var tab in tabs.Values)
            tab.Clear();
    }

    static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    [Fact]
    public async Task A_question_reaches_only_the_recipient_as_the_pages_it_touches()
    {
        await SettledAsync();

        await Browser("Gus").SubmitOnAsync(Page, $"{Page}/ask", Form(("body", "Socks?")));
        await app.Live.SettleAsync();

        var content = Assert.Single(tabs["Rae"].Frames);
        Assert.Equal("content", content.Type);
        var data = Assert.Single(tabs["Rae"].Contents);
        Assert.Equal((ev, nameof(HappeningKind.Question)), (data.Event, data.Area));
        Assert.Equal(["/", Page, $"{Page}/questions"], data.Pages.Keys.Order(StringComparer.Ordinal));
        // The question, with "new", on the questions page; the line on the event page and home; the row in the menu.
        var questions = Parse(data.Pages[$"{Page}/questions"]);
        Assert.Contains("Socks?", questions.QuerySelector("#thread")!.TextContent);
        Assert.Contains("new", questions.QuerySelector("#thread .message-new")!.TextContent);
        Assert.Contains("Your giver asked you something", Parse(data.Pages[Page]).QuerySelector("#live-asked")!.TextContent);
        Assert.Contains("Your giver asked you something", Parse(data.Pages["/"]).QuerySelector($"#event-{ev}")!.TextContent);
        Assert.Contains("Your giver asked you something", questions.QuerySelector("#whats-new-panel .bell-rows")!.TextContent);
        // Only the asker's own tab hears of it otherwise, as a hint; nobody else gets anything.
        Assert.Equal([new LiveHintData(ev, nameof(HappeningKind.Question))], tabs["Gus"].Hints);
        Assert.Empty(tabs["Olga"].Frames);
        Assert.Empty(tabs["Bea"].Frames);
    }

    /// <summary>Only the giver gets content; whoever acted, their own hint; nobody else anything.</summary>
    LiveContentData GiverGot(string area, string actor)
    {
        var data = Assert.Single(tabs["Gus"].Contents);
        Assert.Single(tabs["Gus"].Frames);
        Assert.Equal((ev, area), (data.Event, data.Area));
        Assert.Equal([new LiveHintData(ev, area)], tabs[actor].Hints);
        Assert.All(Names.Where(n => n != "Gus" && n != actor), n => Assert.Empty(tabs[n].Frames));
        return data;
    }

    [Fact]
    public async Task A_reply_reaches_only_the_giver_in_the_question_box()
    {
        await app.MessageAsync(ev, Id("Gus"), MessageSender.Giver, "Socks?");
        await SettledAsync();

        await Browser("Rae").SubmitOnAsync($"{Page}/questions", $"{Page}/questions/reply", Form(("body", "Blue, size 9.")));
        await app.Live.SettleAsync();

        var page = Parse(GiverGot(nameof(HappeningKind.Reply), "Rae").Pages[Page]);
        Assert.Contains("Blue, size 9.", page.QuerySelector("#ask #ask-thread")!.TextContent);
        Assert.Contains("Your recipient replied", page.QuerySelector("#whats-new-panel .bell-rows")!.TextContent);
        // The box as a reload would show it, form and all, with the form's token left for the page to fill in.
        Assert.NotNull(page.QuerySelector($"#ask form[action='{Page}/ask']"));
        Assert.All(page.QuerySelectorAll("input[name='__RequestVerificationToken']"), t => Assert.Equal("", t.GetAttribute("value")));
    }

    [Fact]
    public async Task Closing_and_reopening_reach_only_the_giver_and_say_so_in_the_question_box()
    {
        await app.MessageAsync(ev, Id("Gus"), MessageSender.Giver, "Socks?");
        await SettledAsync();

        await Browser("Rae").SubmitOnAsync($"{Page}/questions", $"{Page}/questions/close");
        await app.Live.SettleAsync();
        var closed = Parse(GiverGot(nameof(HappeningKind.ConversationClosed), "Rae").Pages[Page]);
        await SettledAsync();
        await Browser("Rae").SubmitOnAsync($"{Page}/questions", $"{Page}/questions/reopen");
        await app.Live.SettleAsync();
        var reopened = Parse(GiverGot(nameof(HappeningKind.ConversationReopened), "Rae").Pages[Page]);

        Assert.NotNull(closed.QuerySelector("#ask-closed [data-closed]"));
        Assert.Null(closed.QuerySelector($"#ask form[action='{Page}/ask']"));
        Assert.Contains("Your recipient closed the conversation", closed.QuerySelector("#whats-new-panel .bell-rows")!.TextContent);
        Assert.Null(reopened.QuerySelector("#ask-closed [data-closed]"));
        Assert.NotNull(reopened.QuerySelector($"#ask form[action='{Page}/ask']"));
        Assert.Contains("Your recipient reopened the conversation", reopened.QuerySelector("#whats-new-panel .bell-rows")!.TextContent);
    }

    [Fact]
    public async Task A_patch_reaches_only_the_giver_it_changed_with_the_gift_tied_and_the_new_name_under_it()
    {
        // Four take part, so one can go: Gus gives to Rae, Rae to Bea, Bea to Olga, Olga to Gus.
        // Rae has had her gift open; Bea is taken out, so Rae now gives to Bea's recipient, Olga.
        var four = await app.EventAsync("Mittens", EventStatus.Open, [Id("Olga")], [Id("Gus"), Id("Rae"), Id("Bea"), Id("Olga")]);
        await app.SealAsync(four, Loop(Id("Gus"), Id("Rae"), Id("Bea"), Id("Olga")));
        await app.UpdateAssignmentAsync(four, Id("Rae"), a => a.Viewed = true);
        await SettledAsync();

        await Browser("Olga").PostWithTokenAsync($"/events/{four}/manage", $"/events/{four}/manage/removeperson",
            Form(("personId", Id("Bea").ToString()), ("change", "patch")));
        await app.Live.SettleAsync();

        var data = Assert.Single(tabs["Rae"].Contents);
        Assert.Equal((four, nameof(HappeningKind.Patched)), (data.Event, data.Area));
        var page = Parse(data.Pages[$"/events/{four}"]);
        Assert.Contains("You're giving to someone new", page.QuerySelector("#live-gift-again")!.TextContent);
        var gift = page.QuerySelector("#gift")!;
        Assert.DoesNotContain("untied", gift.ClassList);
        Assert.Equal("Olga", gift.QuerySelector(".revealed-name")!.TextContent);
        Assert.Contains("You're giving to someone new", page.QuerySelector("#whats-new-panel .bell-rows")!.TextContent);
        Assert.All(new[] { "Olga", "Gus" }, n => Assert.Empty(tabs[n].Contents));
    }

    [Fact]
    public async Task A_message_arriving_live_moves_no_marker_and_shows_the_time_a_reload_shows()
    {
        await app.MessageAsync(ev, Id("Gus"), MessageSender.Giver, "Socks?");
        await SettledAsync();
        var before = await app.AssignmentOfAsync(ev, Id("Gus"));

        await Browser("Rae").SubmitOnAsync($"{Page}/questions", $"{Page}/questions/reply", Form(("body", "Blue.")));
        await app.Live.SettleAsync();
        var live = Parse(Assert.Single(tabs["Gus"].Contents).Pages[Page]).QuerySelector("#ask-thread")!;
        var after = await app.AssignmentOfAsync(ev, Id("Gus"));
        var reload = (await Browser("Gus").GetPageAsync(Page)).QuerySelector("#ask-thread")!;

        Assert.Equal(before.GiverReadUpto, after.GiverReadUpto);
        Assert.Equal((before.Viewed, before.RecipientChanged, before.ThreadChangeUnseen), (after.Viewed, after.RecipientChanged, after.ThreadChangeUnseen));
        Assert.Equal(reload.QuerySelectorAll("time").Select(t => t.OuterHtml), live.QuerySelectorAll("time").Select(t => t.OuterHtml));
    }

    [Fact]
    public async Task A_giver_recipient_happening_sets_off_no_request_from_anyone()
    {
        await app.MessageAsync(ev, Id("Gus"), MessageSender.Giver, "Socks?");
        await SettledAsync();
        var requests = app.Logs.Entries.Count(e => e.Category == "Pollyanna.Access");

        await Browser("Rae").SubmitOnAsync($"{Page}/questions", $"{Page}/questions/reply", Form(("body", "Blue.")));
        await app.Live.SettleAsync();

        // Rae's own two requests (the page, the post); the content was rendered in the server and
        // nothing else was fetched. No hint went anywhere but Rae's own tab, and a hint is the only
        // thing that makes a page fetch.
        Assert.Equal(requests + 2, app.Logs.Entries.Count(e => e.Category == "Pollyanna.Access"));
        Assert.All(Names.Where(n => n != "Rae"), n => Assert.Empty(tabs[n].Hints));
    }
}
