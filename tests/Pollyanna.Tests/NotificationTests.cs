using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>Email notifications (spec §15): the outbox, the sender and the "Event is open" email.</summary>
public sealed class NotificationTests : IAsyncLifetime
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

    Task OpenAsync(Guid ev) => browser.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/open");

    [Fact]
    public async Task Opening_an_event_emails_every_participant_but_the_organizer_who_opened_it()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob", "bob@example.com", verified: null);
        var cy = await app.PersonAsync("Cy", "cy@example.com", verified: false);
        var dee = await app.PersonAsync("Dee");
        var otherOrganizer = await app.PersonAsync("Olga", "olga@example.com");
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Draft, [me, otherOrganizer], [me, ann, bob, cy, dee]);
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == bob)).Notes = "Wool socks, size 9";
            await db.SaveChangesAsync();
        });

        await OpenAsync(ev);
        await app.SendMailAsync();

        // Ann's verified email and Bob's unmarked one; never Cy's unverified one, Dee who has none,
        // an organizer who isn't taking part, or Pat, who opened it.
        Assert.Equal(["ann@example.com", "bob@example.com"], app.Mail.Sent.Select(m => m.To).Order());
        var mail = Assert.Single(app.Mail.To("ann@example.com"));
        Assert.Contains("Stockings 2026", mail.Subject);
        Assert.Contains("open", mail.Subject);
        Assert.Contains($"{TestApp.PublicUrl}/events/{ev}", mail.Body);
        Assert.Contains($"{TestApp.PublicUrl}/account", mail.Body);
        Assert.Contains("you take part in", mail.Body);
        Assert.DoesNotContain("Bob", mail.Body);
        Assert.DoesNotContain("Wool socks", mail.Body);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    /// <summary>
    /// Every other test sends with <see cref="TestApp.SendMailAsync"/>; this one runs the sender in
    /// the background as the app does, woken by the save and polling for a retry.
    /// </summary>
    [Fact]
    public async Task The_background_sender_sends_on_its_own_when_woken_and_when_a_retry_is_due()
    {
        await using var live = await TestApp.StartAsync(mail: true, configure: b => b.ConfigureTestServices(s =>
            s.Configure<Pollyanna.Notifications.OutboxSenderOptions>(o => o.SendInBackground = true)));
        var (patBrowser, pat) = await live.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _ = patBrowser;
        var ann = await live.PersonAsync("Ann", "ann@example.com");
        var bob = await live.PersonAsync("Bob", "bob@example.com");
        var first = await live.EventAsync("Stockings", EventStatus.Draft, [pat], [pat, ann]);
        var second = await live.EventAsync("Cookie swap", EventStatus.Draft, [pat], [pat, bob]);

        // Woken by the save.
        await patBrowser.SubmitOnAsync($"/events/{first}/manage", $"/events/{first}/manage/open");
        for (var i = 0; i < 100 && live.Mail.Sent.IsEmpty; i++)
            await Task.Delay(100);
        Assert.Single(live.Mail.To("ann@example.com"));

        // A failed send is picked up again by the poll once its retry is due.
        live.Mail.Failure = _ => new TimeoutException();
        await patBrowser.SubmitOnAsync($"/events/{second}/manage", $"/events/{second}/manage/open");
        for (var i = 0; i < 100 && !live.Logs.Entries.Any(e => e.Message.StartsWith("Couldn't send a notification")); i++)
            await Task.Delay(100);
        live.Mail.Failure = null;
        live.Clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 100 && live.Mail.To("bob@example.com").Count == 0; i++)
        {
            live.Clock.Advance(Pollyanna.Notifications.OutboxSender.PollInterval);
            await Task.Delay(100);
        }
        Assert.Single(live.Mail.To("bob@example.com"));
        Assert.Empty(await live.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Without_a_mail_relay_nothing_is_queued()
    {
        await using var plain = await TestApp.StartAsync();
        var (patBrowser, pat) = await plain.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _ = patBrowser;
        var ann = await plain.PersonAsync("Ann", "ann@example.com");
        var ev = await plain.EventAsync("Stockings", EventStatus.Draft, [pat], [pat, ann]);

        await patBrowser.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/open");
        await plain.SendMailAsync();

        Assert.Equal(EventStatus.Open, await plain.StatusAsync(ev));
        Assert.Empty(await plain.WithDbAsync(db => db.Notifications.ToListAsync()));
        Assert.Empty(plain.Mail.Sent);
    }

    [Fact]
    public async Task A_failed_send_is_retried_with_backoff_and_logged_without_addresses_or_ids()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, ann]);
        app.Mail.Failure = m => new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.RecipientNotAccepted,
            MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable, new MimeKit.MailboxAddress("", m.To), $"5.1.1 <{m.To}>: no such user");

        await OpenAsync(ev);
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
        Assert.Single(await app.WithDbAsync(db => db.Notifications.ToListAsync()));

        // Retried a minute later, then after two, and so on.
        app.Mail.Failure = null;
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
        app.Clock.Advance(TimeSpan.FromSeconds(31));
        await app.SendMailAsync();
        Assert.Single(app.Mail.To("ann@example.com"));
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));

        var failure = Assert.Single(app.Logs.Entries, e => e.Category.StartsWith("Pollyanna.Notifications") && e.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Message;
        Assert.Contains("EventOpen", failure);
        Assert.Contains("550", failure);
        Assert.All(app.Logs.Entries, e =>
        {
            Assert.DoesNotContain("ann@example.com", e.Message);
            Assert.DoesNotContain(ev.ToString(), e.Message);
            Assert.DoesNotContain(ann.ToString(), e.Message);
        });
    }

    [Fact]
    public async Task A_notification_is_dropped_after_24_hours_of_failures()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, ann]);
        app.Mail.Failure = _ => new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);

        await OpenAsync(ev);
        for (var i = 0; i < 25; i++)
        {
            await app.SendMailAsync();
            app.Clock.Advance(TimeSpan.FromHours(1));
        }
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
        Assert.Contains(app.Logs.Entries, e => e.Message.StartsWith("Gave up on a notification (EventOpen) after 24 hours"));

        app.Mail.Failure = null;
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task A_waiting_notification_is_dropped_if_the_person_deletes_their_account_first()
    {
        var (annBrowser, ann) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        using var _ = annBrowser;
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, ann, bob]);
        await OpenAsync(ev);

        var delete = await annBrowser.PostWithTokenAsync("/account/delete", "/account/delete");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, delete.StatusCode);
        await app.SendMailAsync();

        Assert.Equal(["bob@example.com"], app.Mail.Sent.Select(m => m.To));
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task A_waiting_notification_is_dropped_if_the_person_is_no_longer_a_participant()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, ann, bob]);
        await OpenAsync(ev);

        await browser.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/removeperson",
            match: f => f.QuerySelector($"input[name=personId][value='{ann}']") is not null);
        Assert.DoesNotContain(ann, await app.ParticipantsAsync(ev));
        await app.SendMailAsync();

        Assert.Equal(["bob@example.com"], app.Mail.Sent.Select(m => m.To));
    }

    [Fact]
    public async Task An_unreachable_relay_is_logged_and_the_app_starts()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var down = await TestApp.StartAsync(mail: true, configure: b =>
        {
            b.UseSetting(Pollyanna.Configuration.PollyannaSettings.SmtpHostKey, "127.0.0.1");
            b.UseSetting(Pollyanna.Configuration.PollyannaSettings.SmtpPortKey, closedPort.ToString());
            b.ConfigureTestServices(s => s.AddSingleton<Pollyanna.Notifications.IMailSender, Pollyanna.Notifications.SmtpMailSender>());
        });

        using var client = down.NewBrowser();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        for (var i = 0; i < 100 && !down.Logs.Entries.Any(e => e.Message.StartsWith("The mail relay at")); i++)
            await Task.Delay(100);
        var warning = Assert.Single(down.Logs.Entries, e => e.Message.StartsWith("The mail relay at"));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains($"127.0.0.1:{closedPort} can't be reached (network error (ConnectionRefused))", warning.Message);

        // A real send fails the same way and stays in the outbox.
        var (patBrowser, pat) = await down.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _ = patBrowser;
        var ann = await down.PersonAsync("Ann", "ann@example.com");
        var ev = await down.EventAsync("Stockings", EventStatus.Draft, [pat], [pat, ann]);
        await patBrowser.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/open");
        await down.SendMailAsync();
        Assert.Single(await down.WithDbAsync(db => db.Notifications.ToListAsync()));
        Assert.Contains(down.Logs.Entries, e => e.Message == "Couldn't send a notification (EventOpen): network error (ConnectionRefused). It will be retried.");
    }

    [Fact]
    public async Task The_start_says_whether_email_is_on()
    {
        await using var plain = await TestApp.StartAsync();
        Assert.Contains(plain.Logs.Entries, e => e.Message == "Email notifications are off: POLLYANNA_SMTP_HOST is not set.");
        const string on = "Email notifications are on; the mail relay at mail.test:25 answered.";
        for (var i = 0; i < 100 && !app.Logs.Entries.Any(e => e.Message == on); i++)
            await Task.Delay(100); // checked in the background, so a slow relay never holds up the start
        Assert.Contains(app.Logs.Entries, e => e.Message == on);
    }
}
