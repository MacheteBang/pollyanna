using Pollyanna.Data;
using Pollyanna.Happenings;
using Pollyanna.Live;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Who a live signal goes to (spec §16), one case per stored kind, the giver-recipient kinds and
/// your own action, each checked against the live column of <see cref="HappeningKinds.Table"/>.
/// The cast: an organizer, a giver, their recipient, a bystander, someone new to the event, the
/// actor's second tab and someone in another event. Hints name no person, reach only who they
/// should, and other events get nothing.
/// </summary>
public sealed class LiveRoutingTests : IAsyncLifetime
{
    TestApp app = null!;
    readonly Dictionary<string, (TestBrowser Browser, Guid Id)> cast = [];
    Guid other;

    static readonly string[] Names = ["Olga", "Gus", "Rae", "Bea", "Nia", "Xan"];

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        foreach (var name in Names)
            cast[name] = await app.SignInAsync(new MockIdentity(name.ToLowerInvariant(), name, $"{name.ToLowerInvariant()}@example.com", true, null));
        other = await app.EventAsync("Elsewhere", EventStatus.Open, [Id("Xan")], [Id("Xan")]);
    }

    public async Task DisposeAsync()
    {
        foreach (var (browser, _) in cast.Values)
            browser.Dispose();
        await app.DisposeAsync();
    }

    Guid Id(string name) => cast[name].Id;
    TestBrowser Browser(string name) => cast[name].Browser;

    /// <summary>One case: the event's state, what is done and by whom, and whoever else it's about.</summary>
    /// <param name="Joins">Someone the happening brings into the event (or its organizers), so they're in its audience after it.</param>
    /// <param name="Content">Who a giver-recipient happening's content goes to, inside their own connection (#104).</param>
    public sealed record Case(string Name, HappeningKind? Kind, EventStatus Status, bool Sealed, string? Actor, string? About, string? Joins,
        string? Content = null);

    public static TheoryData<string> Cases => [.. All.Select(c => c.Name)];

    static readonly Case[] All =
    [
        new("open", HappeningKind.EventOpened, EventStatus.Draft, false, "Olga", null, null),
        new("draw", HappeningKind.NamesDrawn, EventStatus.Open, false, "Olga", null, null),
        new("reveal", HappeningKind.EventRevealed, EventStatus.Open, true, "Olga", null, null),
        new("add", HappeningKind.AddedToEvent, EventStatus.Open, false, "Olga", "Nia", "Nia"),
        new("accept", HappeningKind.InvitationAccepted, EventStatus.Open, false, null, "Nia", "Nia"),
        new("organizer", HappeningKind.MadeOrganizer, EventStatus.Open, false, "Olga", "Nia", "Nia"),
        new("leave", HappeningKind.RedrawNeeded, EventStatus.Open, true, "Bea", null, null),
        new("question", HappeningKind.Question, EventStatus.Open, true, "Gus", "Rae", null, Content: "Rae"),
        new("reply", HappeningKind.Reply, EventStatus.Open, true, "Rae", null, null, Content: "Gus"),
        new("patched", HappeningKind.Patched, EventStatus.Open, true, "Olga", null, null, Content: "Rae"),
        new("closed", HappeningKind.ConversationClosed, EventStatus.Open, true, "Rae", null, null, Content: "Gus"),
        new("reopened", HappeningKind.ConversationReopened, EventStatus.Open, true, "Rae", null, null, Content: "Gus"),
        new("dismiss", null, EventStatus.Open, false, "Gus", null, null),
        // The event page's own parts (spec §16 "What each page does live").
        new("notes", HappeningKind.NotesEdited, EventStatus.Open, false, "Bea", null, null),
        new("notes drawn", HappeningKind.NotesEdited, EventStatus.Open, true, "Rae", null, null),
        new("details", HappeningKind.DetailsChanged, EventStatus.Open, false, "Olga", null, null),
        new("left", HappeningKind.Left, EventStatus.Open, false, "Bea", "Bea", null),
        new("taken out", HappeningKind.TakenOut, EventStatus.Open, false, "Olga", "Bea", null),
        new("shopping", HappeningKind.DoneShopping, EventStatus.Open, true, "Gus", null, null),
    ];

    [Fact]
    public void Every_kind_says_where_its_signal_goes_and_assignment_ones_never_go_as_a_hint_to_the_event()
    {
        foreach (var kind in new[] { HappeningKind.Question, HappeningKind.Reply, HappeningKind.ConversationClosed,
            HappeningKind.ConversationReopened, HappeningKind.Patched })
            Assert.Equal(LiveTo.Pair, HappeningKinds.Of(kind).Live);
        Assert.All(HappeningKinds.Table.Where(r => r.Stored is not null), r => Assert.NotEqual(LiveTo.Pair, r.Live));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_signal_reaches_only_who_the_table_says_as_a_hint_naming_no_person(string name)
    {
        var c = All.Single(x => x.Name == name);
        // A patch needs four taking part, so one can go: Bea's giver, Rae, then gives to Olga.
        Guid[] taking = c.Name == "patched" ? [Id("Gus"), Id("Rae"), Id("Bea"), Id("Olga")] : [Id("Gus"), Id("Rae"), Id("Bea")];
        var ev = await app.EventAsync("Stockings", c.Status, [Id("Olga")], taking);
        if (c.Sealed)
            await app.SealAsync(ev, Loop(taking));
        if (c.Name == "dismiss")
            await Manage("Olga", ev, "draw");
        if (c.Name is "reply" or "closed" or "reopened")
            await app.MessageAsync(ev, Id("Gus"), MessageSender.Giver, "Socks?");
        if (c.Name == "reopened")
            await app.UpdateAssignmentAsync(ev, Id("Gus"), a => a.ThreadClosed = true);

        var tabs = new Dictionary<string, LiveReader>();
        foreach (var person in Names)
            tabs[person] = await Browser(person).OpenLiveAsync();
        if (c.Actor is { } actorName)
            tabs["second tab"] = await Browser(actorName).OpenLiveAsync();
        await app.Live.SettleAsync();
        foreach (var tab in tabs.Values)
            tab.Clear();

        await ActAsync(c, ev, tabs.GetValueOrDefault(c.Actor ?? ""));
        await app.Live.SettleAsync();

        var expected = Expected(c, tabs.Keys);
        foreach (var (who, tab) in tabs)
        {
            var area = c.Kind?.ToString() ?? "Dismissed";
            Assert.True(expected.Contains(who) == tab.Hints.Contains(new LiveHintData(ev, area)), $"{who}: {string.Join(", ", tab.Hints)} / " + string.Join("; ", tabs.Select(t => t.Key + "=" + string.Join(",", t.Value.Frames))));
            // Taking someone out to patch is public to the event as well, as its own hint.
            Assert.All(tab.Hints.Where(h => !(c.Name == "patched" && h.Area == nameof(HappeningKind.TakenOut))),
                h => Assert.Equal(new LiveHintData(ev, area), h));
            Assert.DoesNotContain(tab.Hints, h => h.Event == other);
            foreach (var frame in tab.Frames.Where(f => f.Type == "hint"))
                foreach (var person in Names)
                    Assert.False(frame.Data.Contains(Id(person).ToString()) || frame.Data.Contains(person), $"{who} got {frame.Data}");
            // Content only for the one person a giver-recipient happening is for, and only as content.
            Assert.Equal(who == c.Content ? [new LiveHintData(ev, c.Kind.ToString()!)] : [],
                tab.Contents.Select(x => new LiveHintData(x.Event, x.Area)));
            Assert.All(tab.Frames, f => Assert.Contains(f.Type, new[] { "hint", "content" }));
        }
        foreach (var tab in tabs.Values)
            tab.Dispose();
    }

    /// <summary>Who should get the hint, by the table's live column for the case's kind.</summary>
    static HashSet<string> Expected(Case c, IEnumerable<string> tabs)
    {
        var to = c.Kind is { } kind ? HappeningKinds.Of(kind).Live : LiveTo.Actor;
        var people = new HashSet<string>();
        if (to.HasFlag(LiveTo.Event))
            people.UnionWith(["Olga", "Gus", "Rae", "Bea"]);
        if (to.HasFlag(LiveTo.Organizers))
            people.Add("Olga");
        if (to.HasFlag(LiveTo.About) && c.About is { } about)
            people.Add(about);
        if (c.Joins is { } joins && (to.HasFlag(LiveTo.Event) || (to.HasFlag(LiveTo.Organizers) && c.Kind == HappeningKind.MadeOrganizer)))
            people.Add(joins);
        if (c.Actor is { } actor)
        {
            people.Add("second tab");
            // A dismissal from the panel names its own tab, which is left out.
            if (c.Kind is not null)
                people.Add(actor);
        }
        return [.. people.Where(tabs.Contains)];
    }

    async Task ActAsync(Case c, Guid ev, LiveReader? actorTab)
    {
        switch (c.Name)
        {
            case "open": await Manage("Olga", ev, "open"); break;
            case "draw": await Manage("Olga", ev, "draw"); break;
            case "reveal": await Manage("Olga", ev, "reveal", ("confirmed", "true")); break;
            case "add": await Manage("Olga", ev, "addpeople", ("email", "nia@example.com")); break;
            case "organizer": await Manage("Olga", ev, "addorganizer", ("email", "nia@example.com")); break;
            case "accept":
                await app.WithDbAsync(db =>
                {
                    db.Invitations.Add(new Invitation { EventId = ev, Email = "nia@example.com", EmailNormalized = "nia@example.com", InvitedBy = Id("Olga") });
                    return db.SaveChangesAsync();
                });
                var (again, _) = await app.SignInAsync(new MockIdentity("nia", "Nia", "nia@example.com", true, null));
                again.Dispose();
                break;
            case "leave":
                var bea = Browser("Bea");
                var second = await TestBrowser.ParseAsync(await bea.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave"));
                await bea.SubmitAsync(second, $"/events/{ev}/leave");
                break;
            case "question":
                await Browser("Gus").SubmitOnAsync($"/events/{ev}", $"/events/{ev}/ask", Form(("body", "Socks?")));
                break;
            case "reply":
                await Browser("Rae").SubmitOnAsync($"/events/{ev}/questions", $"/events/{ev}/questions/reply", Form(("body", "Blue.")));
                break;
            case "patched":
                await Manage("Olga", ev, "removeperson", ("personId", Id("Bea").ToString()), ("change", "patch"));
                break;
            case "closed":
                await Browser("Rae").SubmitOnAsync($"/events/{ev}/questions", $"/events/{ev}/questions/close");
                break;
            case "reopened":
                await Browser("Rae").SubmitOnAsync($"/events/{ev}/questions", $"/events/{ev}/questions/reopen");
                break;
            case "notes":
            case "notes drawn":
                await Browser(c.Actor!).SubmitOnAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", "Blue socks")));
                break;
            case "details":
                await Manage("Olga", ev, "details", ("Name", "Stockings 2027"), ("BudgetText", "$30"),
                    ("ExchangeDate", "2027-01-06"), ("PreviousEventId", ""), ("NoRepeatDepth", "1"));
                break;
            case "left":
                await Browser("Bea").PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/leave");
                break;
            case "taken out": await Manage("Olga", ev, "removeperson", ("personId", Id("Bea").ToString())); break;
            case "shopping":
                await Browser("Gus").SubmitOnAsync($"/events/{ev}", $"/events/{ev}/shopping", Form(("done", "true")));
                break;
            case "dismiss":
                // As the panel does: its own tab named in the header.
                var page = await Browser("Gus").GetPageAsync("/whats-new");
                var token = page.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
                var post = new HttpRequestMessage(HttpMethod.Post, "/whats-new/dismiss")
                {
                    Content = new FormUrlEncodedContent(Form(("event", ev.ToString()), ("kind", nameof(HappeningKind.NamesDrawn)),
                        ("__RequestVerificationToken", token))),
                };
                post.Headers.Add(LiveSignals.TabHeader, actorTab!.Tab);
                await Browser("Gus").App.SendAsync(post);
                break;
        }
    }

    [Fact]
    public async Task A_change_that_rolls_back_sends_nothing_and_what_commits_after_it_still_goes()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [Id("Olga")], [Id("Gus"), Id("Rae"), Id("Bea")]);
        await app.SealAsync(ev, Loop(Id("Gus"), Id("Rae"), Id("Bea")));
        var open = await app.EventAsync("Open one", EventStatus.Open, [Id("Olga")], [Id("Gus"), Id("Bea")]);
        using var olga = await Browser("Olga").OpenLiveAsync();

        // Bea leaves the open event, but taking her out of the drawn one needs a redraw, so the
        // deletion rolls back (her leaving included) and asks the organizers instead.
        await Browser("Bea").SubmitOnAsync("/account/delete", "/account/delete");
        await app.Live.SettleAsync();

        Assert.Contains(app.Happenings.Heard, h => h.Kind == HappeningKind.Left && h.EventId == open);
        Assert.Contains(Id("Bea"), await app.ParticipantsAsync(open));
        Assert.Equal([new LiveHintData(ev, nameof(HappeningKind.RedrawNeeded))], olga.Hints);
    }

    Task<HttpResponseMessage> Manage(string who, Guid ev, string action, params (string Key, string Value)[] fields) =>
        Browser(who).PostWithTokenAsync($"/events/{ev}/manage", $"/events/{ev}/manage/{action}", Form(fields));
}
