using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>The invitation email (spec §15): an invited address with no account yet is asked to sign in and join.</summary>
public sealed class InvitationEmailTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser organizer = null!;
    Guid org;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        (organizer, org) = await app.SignInAsync(new MockIdentity("org", "Olga", "olga@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        organizer.Dispose();
        await app.DisposeAsync();
    }

    Task InviteAsync(Guid ev, string email) =>
        organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/invite", Form(("email", email)));

    Task<List<Notification>> WaitingAsync() => app.WithDbAsync(db => db.Notifications.ToListAsync());

    [Fact]
    public async Task Inviting_an_address_with_no_account_emails_it_an_invitation()
    {
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [org], [org]);

        await InviteAsync(ev, "Sue.M@Example.com");

        // The row holds the address, not a person.
        var row = Assert.Single(await WaitingAsync());
        Assert.Equal(NotificationKind.Invitation, row.Kind);
        Assert.Equal("Sue.M@Example.com", row.Email);
        Assert.Null(row.PersonId);

        await app.SendMailAsync();
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("Sue.M@Example.com", mail.To);
        Assert.Contains("Stockings 2026", mail.Subject);
        Assert.Contains("Olga invited you", mail.Body);
        Assert.Contains("sign in", mail.Body);
        Assert.Contains($"{TestApp.PublicUrl}/events/{ev}", mail.Body);
        Assert.Contains("If this email wasn't meant for you, ignore it.", mail.Body);
        Assert.DoesNotContain("/account", mail.Body);
        Assert.Empty(await WaitingAsync());
    }

    [Fact]
    public async Task A_draft_events_invitation_is_held_until_the_event_opens()
    {
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Draft, [org], [org]);

        await InviteAsync(ev, "sue@example.com");
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await WaitingAsync());

        await organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/open");
        await app.SendMailAsync();

        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("sue@example.com", mail.To);
        Assert.Contains("Olga invited you", mail.Body);
        Assert.Contains("If this email wasn't meant for you, ignore it.", mail.Body);
    }

    [Fact]
    public async Task Inviting_an_address_that_already_belongs_to_a_person_sends_no_invitation()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [org]);

        await InviteAsync(ev, "ANN@example.com");

        // Ann is in the hat straight away; "Added to an event" is hers to get, not an invitation.
        Assert.Contains(ann, await app.ParticipantsAsync(ev));
        Assert.DoesNotContain(await WaitingAsync(), n => n.Kind == NotificationKind.Invitation);
        await app.SendMailAsync();
        Assert.DoesNotContain(app.Mail.Sent, m => m.Body.Contains("wasn't meant for you"));
    }

    [Fact]
    public async Task A_cancelled_invitation_is_dropped_unsent()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [org]);
        await InviteAsync(ev, "sue@example.com");
        Assert.Single(await WaitingAsync());

        await organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/cancelinvite");
        Assert.Empty(await app.WithDbAsync(db => db.Invitations.ToListAsync()));
        await app.SendMailAsync();

        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await WaitingAsync());
    }

    [Fact]
    public async Task An_invitation_accepted_at_sign_in_is_dropped_unsent_and_tells_the_organizer_nothing()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [org]);
        await InviteAsync(ev, "sue@example.com");

        var (sueBrowser, sue) = await app.SignInAsync(new MockIdentity("sue", "Sue", "sue@example.com", true, null));
        sueBrowser.Dispose();
        Assert.Contains(sue, await app.ParticipantsAsync(ev));
        await app.SendMailAsync();

        Assert.DoesNotContain(app.Mail.Sent, m => m.Body.Contains("wasn't meant for you"));
        Assert.Empty(app.Mail.To("olga@example.com"));
        Assert.DoesNotContain(await WaitingAsync(), n => n.Kind == NotificationKind.Invitation);
    }

    [Fact]
    public async Task Accepting_a_sent_invitation_emails_the_organizer_nothing()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [org]);
        await InviteAsync(ev, "sue@example.com");
        await app.SendMailAsync();
        Assert.Single(app.Mail.To("sue@example.com"));

        var (sueBrowser, _) = await app.SignInAsync(new MockIdentity("sue", "Sue", "sue@example.com", true, null));
        sueBrowser.Dispose();
        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.SendMailAsync();

        Assert.Empty(app.Mail.To("olga@example.com"));
    }

    [Fact]
    public async Task Inviting_an_address_again_while_its_email_waits_sends_one_email()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [org], [org]);
        await InviteAsync(ev, "sue@example.com");

        await organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/cancelinvite");
        await InviteAsync(ev, "Sue@Example.com");
        await app.SendMailAsync();

        Assert.Single(app.Mail.Sent);
    }
}
