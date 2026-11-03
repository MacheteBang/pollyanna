using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Pollyanna.Tests;

/// <summary>
/// Changes made while the outbox is in the middle of a run (spec §15 "Sending"): a row deleted
/// before its turn isn't sent, and a row deleted while its email is on the wire doesn't stop the run.
/// </summary>
public sealed class OutboxRunTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    List<string> OutboxErrors() => app.Logs.Entries
        .Where(e => e.Category.StartsWith("Pollyanna.Notifications") && e.Level >= LogLevel.Error)
        .Select(e => e.Message).ToList();

    [Fact]
    public async Task A_redraw_in_the_middle_of_a_run_stops_the_slip_emails_not_yet_sent()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var cy = await app.PersonAsync("Cy", "cy@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob, cy]);
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(3, await app.WithDbAsync(db => db.Notifications.CountAsync()));

        // The organizer redraws while the first slip email is being sent.
        app.Mail.DuringNextSend = async _ =>
            Assert.Equal(HttpStatusCode.Redirect, (await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/redraw")).StatusCode);
        await app.SendMailAsync();

        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Single(app.Mail.Sent);
        Assert.Empty(OutboxErrors());
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Switching_off_while_ones_own_email_is_on_the_wire_does_not_stop_the_run()
    {
        var (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        var (bob, bobId) = await app.SignInAsync(new MockIdentity("bob", "Bob", "bob@example.com", true, null));
        using var _a = ann;
        using var _b = bob;
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId, bobId]);
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/open");

        // Whoever's email goes first switches emails off just then. That one can't be called
        // back (spec §15); the other still goes.
        app.Mail.DuringNextSend = async mail =>
        {
            var who = mail.To == "ann@example.com" ? ann : bob;
            Assert.Equal(HttpStatusCode.Redirect, (await who.SubmitOnAsync("/account", "/account/emails")).StatusCode);
        };
        await app.SendMailAsync();

        Assert.Equal(["ann@example.com", "bob@example.com"], app.Mail.Sent.Select(m => m.To).Order());
        Assert.Empty(OutboxErrors());
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }
}
