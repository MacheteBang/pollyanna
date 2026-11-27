using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The organizing page on "Tablecloth" (spec §7 Organizing an event, §12 Statuses): the head with
/// the switch or a "Home" link and the row of steps, the one next step on the cloth or what blocks
/// it, and the people taking part with words only where something is missing.
/// </summary>
public sealed class OrganizingPageTests : IAsyncLifetime
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

    static readonly string[] Palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];

    static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>The one white panel on the cloth: its sentences, and its buttons and links (label, filled).</summary>
    static (List<string> Text, List<(string Label, bool Filled)> Controls) Panel(IDocument page)
    {
        var panel = Assert.Single(page.QuerySelectorAll(".cloth > .panel"));
        return (
            [.. panel.QuerySelectorAll("p").Select(p => Squash(p.TextContent))],
            [.. panel.QuerySelectorAll(".button, .link-button, a").Select(b => (Squash(b.TextContent), b.ClassList.Contains("button-fill")))]);
    }

    /// <summary>The rows under "Taking part": the name and the small words beside it.</summary>
    static List<(string Name, string Words)> TakingPart(IDocument page) =>
        [.. page.QuerySelectorAll("#people .rows > li").Select(li => (
            Squash(li.QuerySelector(".row-name")!.TextContent),
            Squash(li.QuerySelector(".row-words")?.TextContent ?? "")))];

    async Task<(Guid Ann, Guid Bob, Guid Cy)> ThreeOthersAsync()
    {
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var cy = await app.PersonAsync("Cy");
        return (ann, bob, cy);
    }

    // ---------- Head ----------

    [Fact]
    public async Task The_page_is_on_the_tablecloth_with_a_head_of_title_date_budget_and_the_steps()
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [me], [ann, bob], budget: "$20");

        var page = await browser.GetPageAsync(Manage(ev));

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
        Assert.Contains(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/tablecloth.css"));
        Assert.DoesNotContain(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/site.css"));
        Assert.Empty(page.QuerySelectorAll("[style], style"));
        var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
        Assert.All(hexes, h => Assert.Contains(h, Palette));

        var head = page.QuerySelector(".page-head")!;
        Assert.Equal("Cousins' Swap", Squash(head.QuerySelector("h1")!.TextContent));
        Assert.Contains("$20", head.QuerySelector(".page-head-line")!.TextContent);
        Assert.Contains("Dec 24, 2026", head.QuerySelector(".page-head-line")!.TextContent);
        Assert.Null(page.QuerySelector(".stamp"));
        var steps = head.QuerySelectorAll("ol.steps > li").ToList();
        Assert.Equal(["Draft", "Open", "Names drawn", "Revealed"], steps.Select(s => Squash(s.TextContent)));
        Assert.Equal("Open", Squash(Assert.Single(steps, s => s.GetAttribute("aria-current") == "step").TextContent));
    }

    [Theory]
    [InlineData(EventStatus.Draft, "Draft")]
    [InlineData(EventStatus.Open, "Open")]
    [InlineData(EventStatus.Drawn, "Names drawn")]
    [InlineData(EventStatus.Revealed, "Revealed")]
    public async Task The_current_step_is_marked(EventStatus status, string current)
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var ev = await app.EventAsync("Stockings", status, [me], [ann, bob]);

        var page = await browser.GetPageAsync(Manage(ev));

        var steps = page.QuerySelectorAll("ol.steps > li").ToList();
        Assert.Equal(current, Squash(Assert.Single(steps, s => s.GetAttribute("aria-current") == "step").TextContent));
        Assert.Contains("step-now", Assert.Single(steps, s => s.GetAttribute("aria-current") == "step").ClassList);
    }

    [Fact]
    public async Task An_organizer_who_takes_part_gets_the_switch_and_one_who_doesnt_gets_a_home_link()
    {
        var (ann, bob, _) = await ThreeOthersAsync();
        var mine = await app.EventAsync("Mine too", EventStatus.Open, [me], [me, ann, bob]);
        var theirs = await app.EventAsync("Just organizing", EventStatus.Open, [me], [ann, bob]);

        var page = await browser.GetPageAsync(Manage(mine));
        var sw = page.QuerySelector(".page-head nav.switch")!;
        Assert.Equal(["My page", "Organizing"], sw.Children.Select(c => Squash(c.TextContent)));
        Assert.Equal($"/events/{mine}", sw.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("page", sw.Children[1].GetAttribute("aria-current"));
        Assert.Null(page.QuerySelector(".page-head-back"));

        page = await browser.GetPageAsync(Manage(theirs));
        Assert.Null(page.QuerySelector("nav.switch"));
        var back = page.QuerySelector(".page-head .page-head-back")!;
        Assert.Equal("Home", Squash(back.TextContent));
        Assert.Equal("/", back.GetAttribute("href"));
    }
}
