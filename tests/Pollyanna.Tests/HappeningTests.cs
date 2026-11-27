using Pollyanna.Data;
using Pollyanna.Happenings;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>Every happening is announced at its source, through one entry point (spec §16).</summary>
public sealed class HappeningTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob, cy;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        ann = await app.PersonAsync("Ann", "ann@example.com");
        bob = await app.PersonAsync("Bob", "bob@example.com");
        cy = await app.PersonAsync("Cy", "cy@example.com");
        app.Happenings.Clear();
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    Task Post(Guid ev, string action, IEnumerable<KeyValuePair<string, string>>? fields = null) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/{action}", fields);

    [Fact]
    public async Task Drawing_names_announces_it_naming_who_drew()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob, cy]);

        await Post(ev, "draw");

        Assert.Equal([new Happening(HappeningKind.NamesDrawn, ev, me)], app.Happenings.Heard);
    }
}
