using System.Text.RegularExpressions;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
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

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    public enum Viewer { Organizer, Participant, OrganizerWhoTakesPart }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"[^\"]*\"")]
    private static partial Regex Token();

    /// <summary>Every page the viewer can reach in the event, plus the leave confirmations.</summary>
    static async Task<List<string>> PagesAsync(TestBrowser browser, Guid ev, Viewer viewer, bool archived)
    {
        var paths = new List<string> { "/", "/account", "/account/delete", "/past", $"/events/{ev}" };
        if (viewer != Viewer.Participant)
            paths.Add($"/events/{ev}/manage");
        if (viewer != Viewer.Organizer)
            paths.AddRange([$"/events/{ev}/leave", $"/events/{ev}/questions"]);

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
            }
            if (archived)
                await app.WithDbAsync(async db => { (await db.Events.SingleAsync(e => e.Id == ev)).Status = EventStatus.Archived; await db.SaveChangesAsync(); });
        }

        await SealWithThreadsAsync(first);
        var before = await PagesAsync(browser, ev, viewer, archived);
        await SealWithThreadsAsync(second);
        var after = await PagesAsync(browser, ev, viewer, archived);

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
            Assert.Contains("<p class=\"reveal-name\">Ann</p>", eventPage);
            Assert.Contains("Gloves, please.", eventPage);
            Assert.Contains(archived ? "Your giver asked you" : "Your giver asked you something", eventPage);
            var questions = before.Single(p => p.StartsWith($"/events/{ev}/questions "));
            Assert.Contains("Wide?", questions);
            if (archived)
                Assert.Contains("You got", eventPage);
        }
        else
        {
            Assert.DoesNotContain("reveal-name", string.Concat(before));
            Assert.DoesNotContain("passed-note", string.Concat(before));
        }
        Assert.DoesNotContain(" asks ", string.Concat(before));
        Assert.DoesNotContain("class=\"pairings\"", string.Concat(before));
        if (archived)
            Assert.Contains("Gifts", before.Single(p => p.StartsWith("/past ")));
    }
}
