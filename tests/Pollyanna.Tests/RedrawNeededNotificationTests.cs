using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The "Redraw needed" email (spec §15, §4 "Leaving"): every organizer hears when a request to
/// leave can only be met by a redraw, whether it came from leaving or from deleting an account.
/// </summary>
public sealed class RedrawNeededNotificationTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid olga, oscar, ann, bob, carol, dave;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        olga = await app.PersonAsync("Olga", "olga@example.com");
        oscar = await app.PersonAsync("Oscar", "oscar@example.com");
        ann = await app.PersonAsync("Ann", "ann@example.com");
        bob = await app.PersonAsync("Bob", "bob@example.com");
        carol = await app.PersonAsync("Carol", "carol@example.com");
        dave = await app.PersonAsync("Dave", "dave@example.com");
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    async Task<Guid> DrawnAsync(string name, Guid[] organizers, Guid[] people, Dictionary<Guid, Guid> pairs)
    {
        var ev = await app.EventAsync(name, EventStatus.Open, organizers, people);
        await app.SealAsync(ev, pairs);
        return ev;
    }

    /// <summary>Pat leaves, confirming twice.</summary>
    async Task LeaveAsync(Guid ev)
    {
        var second = await TestBrowser.ParseAsync(await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave"));
        await browser.SubmitAsync(second, $"/events/{ev}/leave");
    }

    [Fact]
    public async Task Leaving_with_too_few_left_emails_every_organizer_that_a_redraw_is_needed()
    {
        var ev = await DrawnAsync("Stockings 2026", [olga, oscar], [me, ann, bob], Loop(me, ann, bob));

        await LeaveAsync(ev);
        await app.SendMailAsync();

        Assert.Equal(["olga@example.com", "oscar@example.com"], app.Mail.Sent.Select(m => m.To).Order());
        var mail = Assert.Single(app.Mail.To("olga@example.com"));
        Assert.Contains("Stockings 2026", mail.Subject);
        Assert.Contains("Pat wants to leave \"Stockings 2026\"; a redraw is needed", mail.Body);
        Assert.Contains($"{TestApp.PublicUrl}/events/{ev}/manage", mail.Body);
        Assert.Contains("you organize", mail.Body);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Leaving_when_the_patch_cant_apply_emails_the_other_organizers_but_not_the_leaver()
    {
        // Pat and Ann drew each other, so Ann can't take over Pat's recipient. Pat organizes too.
        var pairs = new Dictionary<Guid, Guid> { [me] = ann, [ann] = me, [bob] = carol, [carol] = dave, [dave] = bob };
        var ev = await DrawnAsync("Stockings", [me, olga], [me, ann, bob, carol, dave], pairs);

        await LeaveAsync(ev);
        await app.SendMailAsync();

        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("olga@example.com", mail.To);
        Assert.Contains("Pat wants to leave", mail.Body);
        // Nothing about whose slip is whose.
        Assert.DoesNotContain("Ann", mail.Body);
    }

    [Fact]
    public async Task Leaving_by_the_dropout_patch_emails_nobody()
    {
        var ev = await DrawnAsync("Stockings", [olga], [me, ann, bob, carol], Loop(me, ann, bob, carol));

        await LeaveAsync(ev);
        await app.SendMailAsync();

        Assert.DoesNotContain(me, await app.ParticipantsAsync(ev));
        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task Deleting_an_account_that_turns_into_requests_to_leave_emails_those_events_organizers()
    {
        // Only three people in each: taking Pat out leaves too few. Pat organizes the second too.
        await DrawnAsync("First", [olga], [me, ann, bob], Loop(me, ann, bob));
        var second = await DrawnAsync("Second", [me, oscar], [me, carol, dave], Loop(me, carol, dave));
        // An event Pat could simply leave mentions nothing.
        await app.EventAsync("Open one", EventStatus.Open, [olga], [me, ann]);

        await browser.PostWithTokenAsync("/account/delete", "/account/delete");
        await app.SendMailAsync();

        // The account is kept while deletion waits for the redraws, so the emails name Pat.
        Assert.Equal(["olga@example.com", "oscar@example.com"], app.Mail.Sent.Select(m => m.To).Order());
        Assert.Contains("Pat wants to leave \"First\"", Assert.Single(app.Mail.To("olga@example.com")).Body);
        Assert.Contains("Pat wants to leave \"Second\"", Assert.Single(app.Mail.To("oscar@example.com")).Body);
        Assert.Contains($"/events/{second}/manage", Assert.Single(app.Mail.To("oscar@example.com")).Body);
    }

    [Fact]
    public async Task Staying_in_after_all_sends_nothing()
    {
        var ev = await DrawnAsync("Stockings", [olga], [me, ann, bob], Loop(me, ann, bob));
        await LeaveAsync(ev);
        await app.SendMailAsync();
        app.Mail.Sent.Clear();

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/stay");
        await app.SendMailAsync();

        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task A_request_withdrawn_before_its_email_goes_out_drops_the_email()
    {
        var ev = await DrawnAsync("Stockings", [olga], [me, ann, bob], Loop(me, ann, bob));
        await LeaveAsync(ev);

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/stay");
        await app.SendMailAsync();

        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Asking_again_while_the_first_email_still_waits_sends_one_email()
    {
        var ev = await DrawnAsync("Stockings", [olga], [me, ann, bob], Loop(me, ann, bob));
        await LeaveAsync(ev);
        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/stay");

        await LeaveAsync(ev);
        await app.SendMailAsync();

        Assert.Equal(["olga@example.com"], app.Mail.Sent.Select(m => m.To));
    }
}
