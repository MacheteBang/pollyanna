using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The whole app in one look and one set of words (spec §7 Words, §9, §12): every page a person
/// can reach, as a participant and as an organizer in each status, is "Tablecloth", loads only
/// the app's own files, and never says "hat" or "slip" (outside the long about text) or
/// "exchanges" for events. "Paper slips" is gone: its stylesheet and fonts are no longer served.
/// </summary>
public sealed partial class OneLookTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync(providers: [new("google", "Google")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    public enum Viewer { Participant, Organizer, OrganizerWhoTakesPart }

    /// <summary>The long about text (spec §7 About text), where the hat and slips are the real tradition.</summary>
    const string LongAbout = "Every family has a version of this: names on folded slips, a hat going round the table, someone squinting at the handwriting. Around Philadelphia they call it a Pollyanna. This is that, minus the squinting.";

    [GeneratedRegex(@"\b(hats?|slips?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PaperWords();

    [GeneratedRegex(@"\bexchanges\b", RegexOptions.IgnoreCase)]
    private static partial Regex Exchanges();

    /// <summary>What a person reads on a page: its title, its text, and the words in its attributes.</summary>
    static string Words(IHtmlDocument page)
    {
        var attributes = page.QuerySelectorAll("[aria-label], [title], [placeholder], [alt]")
            .SelectMany(e => new[] { "aria-label", "title", "placeholder", "alt" }.Select(a => e.GetAttribute(a)))
            .Where(v => !string.IsNullOrEmpty(v));
        var text = Regex.Replace(page.Body!.TextContent, @"\s+", " ");
        return string.Join(" | ", [page.Title ?? "", text, .. attributes!]).Replace(LongAbout, "");
    }

    static void AssertOneLook(string path, IHtmlDocument page)
    {
        var words = Words(page);
        Assert.False(PaperWords().IsMatch(words), $"{path} says \"{PaperWords().Match(words).Value}\": {words}");
        Assert.False(Exchanges().IsMatch(words), $"{path} says \"exchanges\": {words}");

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")?.GetAttribute("content"));
        var sheets = page.QuerySelectorAll("link[rel=stylesheet]").Select(l => l.GetAttribute("href")!).ToList();
        Assert.True(sheets.Count == 1 && sheets[0].StartsWith("/css/tablecloth.css"), $"{path} loads {string.Join(", ", sheets)}");
        foreach (var el in page.QuerySelectorAll("[src], [href], [action]"))
        {
            var url = el.GetAttribute("src") ?? el.GetAttribute("href") ?? el.GetAttribute("action")!;
            Assert.True((url.StartsWith('/') && !url.StartsWith("//")) || url.StartsWith('#'), $"{path} points at {url}");
        }
    }

    async Task WalkAsync(TestBrowser browser, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var response = await browser.GetFollowingAsync(path);
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, $"{path} answered {(int)response.StatusCode}");
            AssertOneLook(path, await TestBrowser.ParseAsync(response));
        }
    }

    [Theory]
    [MemberData(nameof(EveryStatusAndViewer))]
    public async Task Every_page_a_person_can_reach_is_one_look_with_no_paper_words(EventStatus status, Viewer viewer)
    {
        var (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _b = browser;
        var org = await app.PersonAsync("Org");
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var carol = await app.PersonAsync("Carol");

        Guid[] organizers = viewer == Viewer.Participant ? [org] : [me];
        Guid[] people = viewer == Viewer.Organizer ? [org, ann, bob, carol] : [me, ann, bob, carol];
        var drawn = status is EventStatus.Drawn or EventStatus.Revealed or EventStatus.Archived;
        var ev = await app.EventAsync("Gifts", drawn ? EventStatus.Open : status, organizers, people);
        if (drawn)
        {
            await app.SealAsync(ev, Loop(people));
            foreach (var giver in people)
            {
                await app.MessageAsync(ev, giver, MessageSender.Giver, "Socks or gloves?");
                await app.MessageAsync(ev, giver, MessageSender.Recipient, "Gloves, please.");
                await app.MessageAsync(ev, giver, MessageSender.Giver, "Which color?");
            }
            await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status).SetProperty(e => e.PairingsRevealed, status != EventStatus.Drawn)));
        }
        // An earlier event, so past events and the new-event form have one to show.
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, ann, bob]);

        var paths = new List<string> { "/", "/past", "/account", "/account/delete", "/events/new", $"/events/{ev}", "/no-such-page" };
        if (viewer != Viewer.Participant)
            paths.Add($"/events/{ev}/manage");
        if (viewer != Viewer.Organizer)
            paths.AddRange([$"/events/{ev}/untie", $"/events/{ev}/leave", $"/events/{ev}/questions"]);
        await WalkAsync(browser, paths);

        if (viewer != Viewer.Organizer && status is EventStatus.Open or EventStatus.Drawn)
        {
            // The second leave confirmation, then leaving, and the notice that says so.
            var step = await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave");
            if (step.StatusCode == HttpStatusCode.OK)
            {
                AssertOneLook("leave step 2", await TestBrowser.ParseAsync(step));
                step = await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave", [new("confirmed", "true")]);
            }
            Assert.Equal(HttpStatusCode.Redirect, step.StatusCode);
            var landing = await browser.GetFollowingAsync(step.Headers.Location!.OriginalString);
            var page = await TestBrowser.ParseAsync(landing);
            Assert.NotNull(page.QuerySelector(".notice"));
            AssertOneLook("after leaving", page);
        }
    }

    public static TheoryData<EventStatus, Viewer> EveryStatusAndViewer()
    {
        var data = new TheoryData<EventStatus, Viewer>();
        foreach (var status in Enum.GetValues<EventStatus>())
            foreach (var viewer in Enum.GetValues<Viewer>())
                data.Add(status, viewer);
        return data;
    }

    [Fact]
    public async Task Leaving_after_the_draw_says_someone_is_giving_to_someone_new()
    {
        var (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _b = browser;
        var org = await app.PersonAsync("Org");
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Gifts", EventStatus.Open, [org], [me, ann, bob, carol]);
        await app.SealAsync(ev, Loop(me, ann, bob, carol));

        var left = await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave", [new("confirmed", "true")]);
        var home = await TestBrowser.ParseAsync(await browser.GetFollowingAsync(left.Headers.Location!.OriginalString));

        Assert.Equal("You've left Gifts. One other person is now giving to someone new.", home.QuerySelector(".notice")!.TextContent.Trim());
    }

    [Fact]
    public async Task Asking_when_who_youre_giving_to_cant_be_found_says_so_in_those_words()
    {
        var (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _b = browser;
        var org = await app.PersonAsync("Org");
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var ev = await app.EventAsync("Gifts", EventStatus.Open, [org], [me, ann, bob]);
        await app.SealAsync(ev, Loop(me, ann, bob));
        var page = await browser.GetPageAsync($"/events/{ev}/untie");
        // The draw key changed (spec §5.3): Pat's assignment can't be found any more.
        await app.WithDbAsync(db => db.Assignments.Where(a => a.EventId == ev).ExecuteDeleteAsync());

        var asked = await browser.SubmitAsync(page, $"/events/{ev}/ask", [new("body", "Socks?")]);
        var after = await TestBrowser.ParseAsync(await browser.GetFollowingAsync(asked.Headers.Location!.OriginalString));

        Assert.Equal("We can't find who you're giving to. Ask an organizer to draw names again.", after.QuerySelector(".notice")!.TextContent.Trim());
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/signed-out")]
    [InlineData("/auth/deleted")]
    [InlineData("/auth/failed")]
    [InlineData("/auth/failed?reason=provider")]
    [InlineData("/error")]
    public async Task Signed_out_pages_are_the_same_look(string path)
    {
        using var browser = app.NewBrowser();

        await WalkAsync(browser, [path]);
    }

    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/fonts/courier-prime-latin-400-normal.woff2")]
    [InlineData("/fonts/courier-prime-latin-700-normal.woff2")]
    [InlineData("/fonts/CourierPrime-OFL.txt")]
    [InlineData("/fonts/reenie-beanie-latin-400-normal.woff2")]
    [InlineData("/fonts/ReenieBeanie-OFL.txt")]
    public async Task Paper_slips_files_are_gone(string path)
    {
        // Signed out, any unknown path goes to sign-in; signed in, it's a plain 404.
        var (browser, _) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _b = browser;

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_favicon_is_served_by_the_app()
    {
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync("/favicon.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
    }

    static IEnumerable<string> Preloads(IHtmlDocument page) =>
        page.QuerySelectorAll("link[rel=preload]").Select(l => l.GetAttribute("href")!);

    [Fact]
    public async Task The_hand_is_preloaded_only_where_the_name_can_be_written()
    {
        var (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _b = browser;
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var drawn = await app.EventAsync("Gifts", EventStatus.Open, [me], [me, ann, bob]);
        await app.SealAsync(drawn, Loop(me, ann, bob));
        var open = await app.EventAsync("Later", EventStatus.Open, [me], [me, ann, bob]);

        var gift = await browser.GetPageAsync($"/events/{drawn}");
        var before = await browser.GetPageAsync($"/events/{open}");
        var home = await browser.GetPageAsync("/");

        Assert.Contains(Preloads(gift), href => href.StartsWith("/fonts/playpen-sans-latin-500-normal.woff2"));
        Assert.DoesNotContain(Preloads(before), href => href.Contains("playpen"));
        Assert.DoesNotContain(Preloads(home), href => href.Contains("playpen"));
        // Figtree stays preloaded everywhere.
        Assert.Contains(Preloads(gift), href => href.StartsWith("/fonts/figtree-latin-400-normal.woff2"));
    }
}
