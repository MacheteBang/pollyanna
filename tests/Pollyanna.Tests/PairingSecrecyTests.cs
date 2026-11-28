using System.Text.RegularExpressions;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using AngleSharp.Html.Parser;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// No handler returns another person's recipient before <c>revealed</c>, organizers included
/// (spec §5.5, §9). Each viewer's pages are rendered twice, with everyone else's recipients
/// shuffled in between: if any page depended on someone else's pairing, it would differ. The
/// question threads (spec §6) are refilled each time: the viewer's own two threads the same,
/// everyone else's different, so a page that showed another thread, or who wrote the viewer's
/// own questions, would differ too. An event archived straight from drawn, with its threads
/// kept, stays sealed the same way (spec §4).
/// </summary>
public sealed partial class PairingSecrecyTests : IAsyncLifetime
{
    TestApp app = null!;

    // A second provider, so /account renders its whole "Ways to sign in" section, add buttons included.
    public async Task InitializeAsync() => app = await TestApp.StartAsync(providers: [new("google", "Google")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    public enum Viewer { Organizer, Participant, OrganizerWhoTakesPart }

    /// <summary>Where two renders first differ, with some of each around it, for a failure message.</summary>
    static string Diff(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
            i++;
        var from = Math.Max(0, i - 120);
        return $"\n{a[from..Math.Min(a.Length, i + 200)]}\n---\n{b[from..Math.Min(b.Length, i + 200)]}";
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"[^\"]*\"")]
    private static partial Regex Token();

    /// <summary>
    /// Every page the viewer can reach in the event, plus the leave confirmations, the new-event form
    /// (which lists earlier events), plus the manage
    /// page of <paramref name="upcoming"/>, a draft whose add-people control lists the event's people.
    /// </summary>
    static async Task<List<string>> PagesAsync(TestBrowser browser, Guid ev, Viewer viewer, bool archived, Guid upcoming)
    {
        var paths = new List<string> { "/", "/account", "/account/delete", "/past", "/whats-new", "/events/new", $"/events/{ev}", $"/events/{upcoming}/manage" };
        if (viewer != Viewer.Participant)
            paths.Add($"/events/{ev}/manage");
        if (viewer != Viewer.Organizer)
            paths.AddRange([$"/events/{ev}/untie", $"/events/{ev}/leave", $"/events/{ev}/questions"]);

        var pages = new List<string>();
        foreach (var path in paths)
        {
            var response = await browser.GetFollowingAsync(path);
            pages.Add($"{path} {(int)response.StatusCode}\n{await response.Content.ReadAsStringAsync()}");
        }
        if (viewer != Viewer.Organizer && !archived)
        {
            // The second leave confirmation is the only page a POST renders.
            var second = await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave");
            pages.Add($"leave step 2 {(int)second.StatusCode}\n{await second.Content.ReadAsStringAsync()}");
        }
        return [.. pages.Select(p => Token().Replace(p, ""))];
    }

    [Theory]
    [InlineData(Viewer.Organizer, false)]
    [InlineData(Viewer.Participant, false)]
    [InlineData(Viewer.OrganizerWhoTakesPart, false)]
    [InlineData(Viewer.Organizer, true)]
    [InlineData(Viewer.Participant, true)]
    [InlineData(Viewer.OrganizerWhoTakesPart, true)]
    public async Task Pages_dont_change_when_other_peoples_recipients_do(Viewer viewer, bool archived)
    {
        var (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _b = browser;
        var org = await app.PersonAsync("Org");
        var a = await app.PersonAsync("Ann");
        var b = await app.PersonAsync("Bob");
        var c = await app.PersonAsync("Carol");
        var d = await app.PersonAsync("Dave");

        Guid[] organizers = viewer == Viewer.Participant ? [org] : [me];
        Guid[] people = viewer == Viewer.Organizer ? [a, b, c, d, org] : [me, a, b, c, d];
        var ev = await app.EventAsync("Gifts", EventStatus.Open, organizers, people);
        var upcoming = await app.EventAsync("Next year", EventStatus.Draft, [me], [me]);
        var (p0, p1, p2, p3, p4) = (people[0], people[1], people[2], people[3], people[4]);

        // The first person in the list keeps their recipient (that's Pat when Pat takes part);
        // everyone else's changes.
        var first = Loop(p0, p1, p2, p3, p4);
        var second = viewer == Viewer.Organizer
            ? Loop(p4, p3, p2, p1, p0)
            : new Dictionary<Guid, Guid> { [p0] = p1, [p1] = p4, [p2] = p0, [p3] = p2, [p4] = p3 };
        Assert.All(people.Where(p => p != me), p => Assert.NotEqual(first[p], second[p]));

        async Task SealWithThreadsAsync(Dictionary<Guid, Guid> pairs)
        {
            await app.SealAsync(ev, pairs);
            foreach (var (giver, recipient) in pairs)
            {
                if (recipient == me)
                {
                    await app.MessageAsync(ev, giver, MessageSender.Giver, "What's your shoe size?");
                    await app.MessageAsync(ev, giver, MessageSender.Recipient, "Nine.");
                    await app.MessageAsync(ev, giver, MessageSender.Giver, "Wide?");
                }
                else if (giver == me)
                {
                    await app.MessageAsync(ev, giver, MessageSender.Giver, "Socks or gloves?");
                    await app.MessageAsync(ev, giver, MessageSender.Recipient, "Gloves, please.");
                }
                else
                {
                    await app.MessageAsync(ev, giver, MessageSender.Giver, $"{giver} asks {recipient}");
                    await app.UpdateAssignmentAsync(ev, giver, a => { a.ThreadClosed = true; a.RecipientReadUpto = 1; });
                }
                // Every giver, the viewer included, has all of "What's new"'s worked-out kinds
                // waiting (spec §16): patched, notes updated, closed; only the viewer's may show.
                await app.UpdateAssignmentAsync(ev, giver, a =>
                {
                    a.Viewed = true;
                    a.RecipientChanged = true;
                    a.NotesVersionSeen = -1;
                    a.ThreadClosed = true;
                    a.ThreadChangeUnseen = true;
                });
            }
            if (archived)
                await app.WithDbAsync(async db => { (await db.Events.SingleAsync(e => e.Id == ev)).Status = EventStatus.Archived; await db.SaveChangesAsync(); });
        }

        await SealWithThreadsAsync(first);
        var before = await PagesAsync(browser, ev, viewer, archived, upcoming);
        await SealWithThreadsAsync(second);
        var after = await PagesAsync(browser, ev, viewer, archived, upcoming);

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Equal(before[i], after[i]);

        // Pollyanna is a gift exchange for any occasion: no Santas, no stockings.
        Assert.All(before, page =>
        {
            Assert.DoesNotContain("santa", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stocking", page, StringComparison.OrdinalIgnoreCase);
        });

        // And the participant really does see their own recipient and both their threads.
        if (viewer != Viewer.Organizer)
        {
            var eventPage = before.Single(p => p.StartsWith($"/events/{ev} "));
            if (archived)
                Assert.Contains("<p class=\"over-line\">You gave to <strong>Ann</strong></p>", eventPage);
            else
                Assert.Contains("<p class=\"revealed-name\" tabindex=\"-1\">Ann</p>", eventPage);
            Assert.Contains("Gloves, please.", eventPage);
            Assert.Contains(archived ? "Your giver asked you" : "Your giver asked you something", eventPage);
            var questions = before.Single(p => p.StartsWith($"/events/{ev}/questions "));
            Assert.Contains("Wide?", questions);
            if (archived)
                Assert.Contains("The pairings were never revealed", eventPage);
        }
        else
        {
            Assert.DoesNotContain("reveal-name", string.Concat(before));
            Assert.DoesNotContain("revealed-name", string.Concat(before));
            Assert.DoesNotContain("class=\"bubble\"", string.Concat(before));
        }
        // The add-people control on the draft lists the event's people.
        Assert.Contains($"for=\"add-{a}\"", before.Single(p => p.StartsWith($"/events/{upcoming}/manage ")));
        Assert.DoesNotContain(" asks ", string.Concat(before));
        Assert.DoesNotContain("class=\"pairings\"", string.Concat(before));
        Assert.DoesNotContain("who-drew-whom", string.Concat(before));
        Assert.DoesNotContain(" gave to you", string.Concat(before));
        if (archived)
            Assert.Contains("Gifts", before.Single(p => p.StartsWith("/past ")));

        // Home's waiting lines are covered: the unread question while drawn, the organizer's line for the draft.
        var home = before.Single(p => p.StartsWith("/ "));
        Assert.Contains("Ready to open", home);
        if (viewer != Viewer.Organizer && !archived)
            Assert.Contains("Your giver asked you something", home);

        // "What's new" and the bell are covered: the viewer's own five worked-out kinds while drawn.
        var whatsNew = before.Single(p => p.StartsWith("/whats-new "));
        string[] lines = ["Your giver asked you something", "Your recipient replied", "Your recipient closed the conversation",
            "You&#x27;re giving to someone new", "Your recipient updated their notes"];
        if (viewer != Viewer.Organizer && !archived)
        {
            Assert.All(lines, line => Assert.Contains(line, whatsNew));
            Assert.Contains("What&#x27;s new, 5 notifications", home);
        }
        else
            Assert.All(lines, line => Assert.DoesNotContain(line, whatsNew));
    }

    /// <summary>
    /// Live (spec §16 Testing): a fixed script of giver-recipient happenings, every giver asking
    /// and every recipient replying, closing and reopening, through the pages, with the viewer's
    /// page open throughout. Run twice with everyone else's pairings shuffled, the viewer's whole
    /// frame sequence and their "What's new" (masthead included) come out the same. A participant
    /// gets their own question, and their own recipient's reply, closing and reopening, as content;
    /// an organizer who doesn't take part gets nothing at all.
    /// </summary>
    [Theory]
    [InlineData(Viewer.Participant)]
    [InlineData(Viewer.Organizer)]
    public async Task Live_frames_dont_change_when_other_peoples_recipients_do(Viewer viewer)
    {
        var cast = new Dictionary<string, (TestBrowser Browser, Guid Id)>();
        foreach (var name in new[] { "Pat", "Ann", "Bob", "Carol", "Dave", "Org" })
            cast[name] = await app.SignInAsync(new MockIdentity(name.ToLowerInvariant(), name, $"{name.ToLowerInvariant()}@example.com", true, null));
        try
        {
            // Pat first, so the order of Pat's own actions among everyone's stays put.
            string[] taking = viewer == Viewer.Participant ? ["Pat", "Ann", "Bob", "Carol", "Dave"] : ["Ann", "Bob", "Carol", "Dave", "Pat"];
            var me = cast[viewer == Viewer.Participant ? "Pat" : "Org"];
            var (p0, p1, p2, p3, p4) = (cast[taking[0]].Id, cast[taking[1]].Id, cast[taking[2]].Id, cast[taking[3]].Id, cast[taking[4]].Id);
            var first = Loop(p0, p1, p2, p3, p4);
            var second = viewer == Viewer.Organizer
                ? Loop(p4, p3, p2, p1, p0)
                : new Dictionary<Guid, Guid> { [p0] = p1, [p1] = p4, [p2] = p0, [p3] = p2, [p4] = p3 };

            async Task<(List<string> Frames, string WhatsNew)> RunAsync(Dictionary<Guid, Guid> pairs)
            {
                var ev = await app.EventAsync("Gifts", EventStatus.Open, [cast["Org"].Id], [.. taking.Select(n => cast[n].Id)]);
                await app.SealAsync(ev, pairs);
                var page = $"/events/{ev}";
                using var live = await me.Browser.OpenLiveAsync();
                await app.Live.SettleAsync();
                live.Clear();

                foreach (var name in taking)
                    await cast[name].Browser.SubmitOnAsync(page, $"{page}/ask", Form(("body", "A question")));
                foreach (var name in taking)
                {
                    var browser = cast[name].Browser;
                    await browser.SubmitOnAsync($"{page}/questions", $"{page}/questions/reply", Form(("body", "A reply")));
                    await browser.SubmitOnAsync($"{page}/questions", $"{page}/questions/close");
                    await browser.SubmitOnAsync($"{page}/questions", $"{page}/questions/reopen");
                }
                await app.Live.SettleAsync();

                var frames = live.Script(ev);
                var whatsNew = Regex.Replace(Token().Replace(await (await me.Browser.GetAsync("/whats-new")).Content.ReadAsStringAsync(), ""),
                    "<time datetime=\"[^\"]*\"[^>]*>[^<]*</time>", "<time/>").Replace(ev.ToString(), "{event}");
                // Gone before the next run, so home holds only this run's event.
                await app.WithDbAsync(async db => { db.Events.Remove(await db.Events.SingleAsync(e => e.Id == ev)); await db.SaveChangesAsync(); });
                return (frames, whatsNew);
            }

            var before = await RunAsync(first);
            var after = await RunAsync(second);

            Assert.Equal(before.Frames, after.Frames);
            Assert.Equal(before.WhatsNew, after.WhatsNew);
            var contents = before.Frames.Where(f => f.StartsWith("content ")).Select(f => f.Split('\n')[0]).ToList();
            if (viewer == Viewer.Participant)
                Assert.Equal(["content Question", "content Reply", "content ThreadClosed", "content ThreadReopened"], contents);
            else
                Assert.Empty(before.Frames);
        }
        finally
        {
            foreach (var (browser, _) in cast.Values)
                browser.Dispose();
        }
    }

    /// <summary>
    /// A hint sets off a live refetch from everyone it reaches, so what comes back must depend on
    /// nobody's pairing, the viewer's own included (spec §16 "A live refetch isn't a visit"): a
    /// response that changed only for one giver would pair them in the proxy log by its size.
    /// After a notes edit, a patch taking someone out and a patch bringing someone in, in a drawn
    /// event, every person's hint refetch of each live page comes out byte-identical across two
    /// runs with every pairing different. The change reaches the one giver as content instead.
    /// </summary>
    [Fact]
    public async Task A_hint_refetch_doesnt_change_with_anyones_pairing_and_the_giver_gets_the_change_as_content()
    {
        var cast = new Dictionary<string, (TestBrowser Browser, Guid Id)>();
        foreach (var name in new[] { "Pat", "Ann", "Bob", "Carol", "Dave", "Org", "Eve" })
            cast[name] = await app.SignInAsync(new MockIdentity(name.ToLowerInvariant(), name, $"{name.ToLowerInvariant()}@example.com", true, null));
        Guid Id(string name) => cast[name].Id;
        try
        {
            // Eve is known to Org, so Org can bring her in late.
            await app.EventAsync("Known", EventStatus.Open, [Id("Org")], [Id("Org"), Id("Eve")]);
            string[] taking = ["Pat", "Ann", "Bob", "Carol", "Dave"];
            // Ann's giver is Pat, then Bob; Dave's is Carol, then Ann.
            var first = Loop([.. taking.Select(Id)]);
            var second = Loop(Id("Pat"), Id("Bob"), Id("Ann"), Id("Dave"), Id("Carol"));
            Assert.All(taking, n => Assert.NotEqual(first[Id(n)], second[Id(n)]));

            async Task<(List<string> Pages, List<string> Contents)> RunAsync(Dictionary<Guid, Guid> pairs)
            {
                var ev = await app.EventAsync("Gifts", EventStatus.Open, [Id("Org"), Id("Pat")], [.. taking.Select(Id)]);
                await app.SealAsync(ev, pairs);
                foreach (var giver in pairs.Keys)
                    await app.UpdateAssignmentAsync(ev, giver, a => a.Viewed = true);
                string Plain(string s) => Regex.Replace(Token().Replace(s, ""), "<time datetime=\"[^\"]*\"[^>]*>[^<]*</time>", "<time/>")
                    .Replace(ev.ToString(), "{event}");
                var page = $"/events/{ev}";
                var manage = $"{page}/manage";
                var tabs = new Dictionary<string, LiveReader>();
                foreach (var name in cast.Keys)
                    tabs[name] = await cast[name].Browser.OpenLiveAsync();
                await app.Live.SettleAsync();
                foreach (var tab in tabs.Values)
                    tab.Clear();

                var pages = new List<string>();
                var contents = new List<string>();
                async Task AfterAsync(string step)
                {
                    await app.Live.SettleAsync();
                    foreach (var (name, (browser, _)) in cast)
                        foreach (var path in new[] { "/", page, $"{page}/questions", manage })
                        {
                            var response = await browser.RefetchAsync(path, hint: true);
                            pages.Add(Plain($"{step}: {name} {path} {(int)response.StatusCode}\n{await response.Content.ReadAsStringAsync()}"));
                        }
                    // Only the one giver gets the content, with the change in it.
                    foreach (var (name, tab) in tabs)
                        foreach (var content in tab.Contents)
                        {
                            var html = new HtmlParser().ParseDocument(content.Pages[page]);
                            contents.Add($"{step}: {name} {content.Area} {html.QuerySelector(".revealed-name")?.TextContent} / {html.QuerySelector("#their-notes .notes")?.TextContent}");
                        }
                    foreach (var tab in tabs.Values)
                        tab.Clear();
                }

                await cast["Ann"].Browser.SubmitOnAsync(page, $"{page}/notes", Form(("notes", "Blue socks, size 9")));
                await AfterAsync("notes");
                await cast["Org"].Browser.PostWithTokenAsync(manage, $"{manage}/removeperson", Form(("personId", Id("Dave").ToString()), ("change", "patch")));
                await AfterAsync("taken out");
                await cast["Org"].Browser.PostWithTokenAsync(manage, $"{manage}/addperson",
                    Form(("personId", Id("Eve").ToString()), ("change", "Patch"), ("householdId", "")));
                await AfterAsync("brought in");

                foreach (var tab in tabs.Values)
                    tab.Dispose();
                // A real visit takes up any notice still waiting (a refetch leaves it), so none is left for the next run.
                foreach (var (browser, _) in cast.Values)
                    await browser.GetAsync("/");
                // Gone before the next run, so home holds only this run's event.
                await app.WithDbAsync(async db => { db.Events.Remove(await db.Events.SingleAsync(e => e.Id == ev)); await db.SaveChangesAsync(); });
                return (pages, contents);
            }

            var before = await RunAsync(first);
            var after = await RunAsync(second);

            Assert.Equal(before.Pages.Count, after.Pages.Count);
            for (var i = 0; i < before.Pages.Count; i++)
                Assert.True(before.Pages[i] == after.Pages[i], $"{before.Pages[i].Split((char)10)[0]}: {Diff(before.Pages[i], after.Pages[i])}");
            // The refetch did leave the pairs out, and the giver's own content carried them.
            var patsPage = new HtmlParser().ParseDocument(before.Pages.Single(p => p.StartsWith("notes: Pat /events/{event} ")));
            Assert.NotNull(patsPage.QuerySelector("#their-notes[data-live-withheld]"));
            Assert.NotNull(patsPage.QuerySelector("#ask[data-live-withheld]"));
            Assert.Equal("", patsPage.QuerySelector(".revealed-name")!.TextContent);
            Assert.Contains("notes: Pat NotesEdited Ann / Blue socks, size 9", before.Contents);
            Assert.Contains("notes: Bob NotesEdited Ann / Blue socks, size 9", after.Contents);
            Assert.Contains("taken out: Carol Patched Pat / No notes yet.", before.Contents);
            Assert.Contains("taken out: Ann Patched Carol / No notes yet.", after.Contents);
            Assert.Single(before.Contents, c => c.StartsWith("brought in: ") && c.Contains(" Patched Eve "));
            Assert.Single(after.Contents, c => c.StartsWith("brought in: ") && c.Contains(" Patched Eve "));
        }
        finally
        {
            foreach (var (browser, _) in cast.Values)
                browser.Dispose();
        }
    }
}
