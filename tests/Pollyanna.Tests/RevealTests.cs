using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>
/// The event page after the draw in the "Tablecloth" identity (spec §7 Event page, after the draw;
/// §12 The reveal; §5.2 the viewed flag): the gift tied with the bow, what sits around it, and how
/// it unties with and without the script.
/// </summary>
public sealed class RevealTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob, org;
    Guid ev;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("sam", "Sam", "sam@example.com", true, null));
        org = await app.PersonAsync("Org");
        ann = await app.PersonAsync("Ann");
        bob = await app.PersonAsync("Bob");
        ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [me, ann, bob]);
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == ann)).Notes = "Lemon anything";
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == me)).Notes = "Warm socks, size 9. No candles please.";
            await db.SaveChangesAsync();
        });
        await app.SealAsync(ev, Seed.Loop(me, ann, bob));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    string Page => $"/events/{ev}";

    static string Text(IElement? e) => Regex.Replace(e?.TextContent ?? "", @"\s+", " ").Trim();

    [Fact]
    public async Task Tied_the_whole_band_is_the_gift_with_ribbons_and_the_bow_and_one_filled_button_below()
    {
        var page = await browser.GetPageAsync(Page);

        Assert.Null(page.QuerySelector("link[href^='/css/site.css']"));
        Assert.NotNull(page.QuerySelector("link[href^='/css/tablecloth.css']"));
        var gift = page.QuerySelector("#gift")!;
        Assert.False(gift.ClassList.Contains("untied"));
        var cloth = gift.QuerySelector(".cloth")!;
        Assert.NotNull(cloth.QuerySelector(".gift-ribbon-across"));
        Assert.NotNull(cloth.QuerySelector(".gift-ribbon-down"));
        Assert.NotNull(cloth.QuerySelector("svg.gift-bow"));
        // Tapping the gift unties it too.
        Assert.NotNull(cloth.QuerySelector($"a.gift-cover[href='{Page}/untie#gift']"));
        var button = gift.QuerySelector(".gift-under a.button")!;
        Assert.Equal(("Untie the bow", $"{Page}/untie#gift"), (Text(button), button.GetAttribute("href")));
        Assert.True(button.ClassList.Contains("button-fill"));
        Assert.Single(page.QuerySelectorAll("main .button-fill"));
        // The name is under the cloth for the script to show, under "You're giving to".
        Assert.Equal("You're giving to", Text(gift.QuerySelector(".gift-to")));
        Assert.Equal("Ann", Text(gift.QuerySelector(".revealed-name")));
    }

    [Fact]
    public async Task The_first_untying_comes_from_the_viewed_flag_and_later_visits_are_shorter()
    {
        Assert.Equal("true", (await browser.GetPageAsync(Page)).QuerySelector("#gift")!.GetAttribute("data-first"));

        await browser.SubmitOnAsync(Page, $"{Page}/seen");

        Assert.Equal("false", (await browser.GetPageAsync(Page)).QuerySelector("#gift")!.GetAttribute("data-first"));
    }

    [Fact]
    public async Task Without_the_script_the_name_shows_untied_on_the_cloth_and_hiding_it_records_the_viewing()
    {
        var page = await browser.GetPageAsync($"{Page}/untie");

        var gift = page.QuerySelector("#gift")!;
        Assert.True(gift.ClassList.Contains("untied"));
        var panel = gift.QuerySelector(".cloth > .panel")!;
        Assert.Equal("Ann", Text(panel.QuerySelector(".revealed-name")));
        Assert.Contains("Lemon anything", Text(panel));
        Assert.NotNull(panel.QuerySelector($"form[action='{Page}/ask'] textarea"));
        Assert.NotNull(panel.QuerySelector($"form[action='{Page}/shopping'] input[type=checkbox][name=done]"));
        var hide = panel.QuerySelector($"form[action='{Page}/seen'] button")!;
        Assert.Equal("Hide the name", Text(hide));
        Assert.False((await app.AssignmentOfAsync(ev, me)).Viewed);

        var response = await browser.SubmitAsync(page, $"{Page}/seen");

        Assert.Equal($"{Page}#gift", response.Headers.Location!.OriginalString);
        Assert.True((await app.AssignmentOfAsync(ev, me)).Viewed);
        // Every visit starts tied.
        Assert.False((await browser.GetPageAsync(Page)).QuerySelector("#gift")!.ClassList.Contains("untied"));
    }
}
