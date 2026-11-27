using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.Tests.Support;

namespace Pollyanna.Tests;

/// <summary>
/// Home in the "Tablecloth" identity (spec §7 Home, Empty home, Past events): one panel per
/// event with the one thing waiting on the viewer, the empty home in two halves, and the plain
/// list of past events.
/// </summary>
public sealed class HomeTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    /// <summary>Each event's panel on the cloth: its name, and its waiting line and where it links (null without one).</summary>
    static List<(string Name, string? Line, string? Href)> Panels(IDocument page) =>
        [.. page.QuerySelectorAll(".cloth > .panel").Select(p =>
        {
            var line = p.QuerySelector(".waiting");
            return (p.QuerySelector(".panel-title")!.TextContent.Trim(), line?.TextContent.Trim(), line?.GetAttribute("href"));
        })];

    async Task<(TestBrowser Browser, Guid Me, Guid A, Guid B)> MeAndTwoAsync()
    {
        var (browser, me) = await app.SignInAsync(new("me", "Sam Rivera", "sam@pollyanna.example", true, null));
        return (browser, me, await app.PersonAsync("Ana"), await app.PersonAsync("Kofi"));
    }

    [Fact]
    public async Task Open_without_notes_asks_for_notes_and_links_to_the_event()
    {
        var (browser, me, a, b) = await MeAndTwoAsync();
        using var _b = browser;
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [a], [me, a, b]);

        var page = await browser.GetPageAsync("/");

        Assert.Equal([("Cousins' Swap", "Write your notes", $"/events/{ev}")], Panels(page));
    }
}
