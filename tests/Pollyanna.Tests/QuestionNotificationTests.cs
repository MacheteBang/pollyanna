using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// "A question for you" (spec §15, docs/adr/0001 and 0004): the recipient is emailed when their
/// giver asks, held to the next full hour, one email per waiting row, dropped if read first; the
/// giver is never emailed about their own thread.
/// </summary>
public sealed class QuestionNotificationTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!, ann = null!;
    Guid patId, annId, bob, carol;
    Guid ev;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        app.Clock.SetUtcNow(new DateTimeOffset(Tomorrow.At(10, 47, 12)));
        (pat, patId) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        bob = await app.PersonAsync("Bob", "bob@example.com");
        carol = await app.PersonAsync("Carol", "carol@example.com");
        // Pat gives to Ann; Carol is Pat's giver.
        ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [bob], [patId, annId, bob, carol]);
        await app.SealAsync(ev, Loop(patId, annId, bob, carol));
        await app.WithDbAsync(async db =>
        {
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == annId)).Notes = "Wool socks, size 9";
            await db.SaveChangesAsync();
        });
    }

    public async Task DisposeAsync()
    {
        pat.Dispose();
        ann.Dispose();
        await app.DisposeAsync();
    }

    string Page => $"/events/{ev}";
    string Questions => $"/events/{ev}/questions";

    Task AskAsync(string body) => pat.SubmitOnAsync(Page, $"{Page}/ask", Form(("body", body)));

    [Fact]
    public async Task A_question_is_emailed_to_the_recipient_at_the_next_full_hour_without_the_question_or_the_giver()
    {
        await AskAsync("What's your shoe size?");
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);

        app.Clock.SetUtcNow(new DateTimeOffset(Tomorrow.At(10, 59, 59)));
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);

        app.Clock.SetUtcNow(new DateTimeOffset(Tomorrow.At(11, 0, 0)));
        await app.SendMailAsync();
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("ann@example.com", mail.To);
        Assert.Contains("Stockings 2026", mail.Subject);
        Assert.Contains("question", mail.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{TestApp.PublicUrl}/events/{ev}/questions", mail.Body);
        Assert.Contains($"{TestApp.PublicUrl}/account", mail.Body);
        Assert.DoesNotContain("shoe size", mail.Subject + mail.Body);
        Assert.DoesNotContain("Pat", mail.Subject + mail.Body);
        Assert.DoesNotContain("Wool socks", mail.Body);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }
    void At(int hour, int minute) => app.Clock.SetUtcNow(new DateTimeOffset(Tomorrow.At(hour, minute, 0)));

    [Fact]
    public async Task Questions_asked_while_an_email_waits_join_it_and_later_ones_get_their_own()
    {
        await AskAsync("What's your shoe size?");
        At(10, 58);
        await AskAsync("And your favourite colour?");
        Assert.Single(await app.WithDbAsync(db => db.Notifications.ToListAsync()));

        At(11, 0);
        await app.SendMailAsync();
        Assert.Single(app.Mail.To("ann@example.com"));

        At(11, 20);
        await AskAsync("Tea or coffee?");
        At(11, 59);
        await app.SendMailAsync();
        Assert.Single(app.Mail.Sent);
        At(12, 0);
        await app.SendMailAsync();
        Assert.Equal(2, app.Mail.To("ann@example.com").Count());
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task The_email_is_dropped_if_the_recipient_reads_the_thread_first()
    {
        await AskAsync("What's your shoe size?");
        At(10, 50);
        await ann.GetPageAsync(Questions);

        At(11, 0);
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task A_question_asked_after_the_recipient_read_the_earlier_ones_is_still_emailed()
    {
        await AskAsync("What's your shoe size?");
        At(10, 50);
        await ann.GetPageAsync(Questions);
        At(10, 55);
        await AskAsync("And your favourite colour?");

        At(11, 0);
        await app.SendMailAsync();
        Assert.Single(app.Mail.To("ann@example.com"));
    }
    [Fact]
    public async Task A_reply_or_closing_and_reopening_the_thread_never_emails_the_giver()
    {
        await AskAsync("What's your shoe size?");
        At(10, 50);
        await ann.GetPageAsync(Questions);
        await ann.SubmitOnAsync(Questions, $"{Questions}/reply", Form(("body", "Nine")));
        await ann.SubmitOnAsync(Questions, $"{Questions}/close");
        await ann.SubmitOnAsync(Questions, $"{Questions}/reopen");
        await ann.SubmitOnAsync(Questions, $"{Questions}/reply", Form(("body", "Also, blue")));
        Assert.Equal(3, (await app.ThreadOfAsync(ev, patId)).Count);
        Assert.False((await app.AssignmentOfAsync(ev, patId)).ThreadClosed);

        Assert.Empty(await app.WithDbAsync(db => db.Notifications.Where(n => n.PersonId != annId).ToListAsync()));
        At(13, 0);
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task The_waiting_row_holds_only_the_event_and_the_recipient_and_failures_log_nothing_that_finds_the_thread()
    {
        await AskAsync("What's your shoe size?");

        // The assignment row already pairs the event with Ann; nothing here points at Pat, the
        // thread or the message, and no time is kept but the hour it goes out.
        var row = Assert.Single(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
        Assert.Equal(ev, row.EventId);
        Assert.Equal(annId, row.PersonId);
        Assert.Null(row.NamedPersonId);
        Assert.Null(row.Email);
        Assert.Equal(Tomorrow.At(11), row.NotBefore);

        app.Mail.Failure = _ => new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
        At(11, 0);
        await app.SendMailAsync();
        app.Mail.Failure = null;
        At(11, 2);
        await app.SendMailAsync();
        Assert.Single(app.Mail.To("ann@example.com"));

        var assignment = await app.AssignmentOfAsync(ev, patId);
        var message = Assert.Single(await app.ThreadOfAsync(ev, patId));
        Assert.Contains(app.Logs.Entries, e => e.Message.Contains("Question"));
        Assert.All(app.Logs.Entries, e =>
        {
            foreach (var secret in new[] { "ann@example.com", ev.ToString(), annId.ToString(), patId.ToString(), assignment.Id.ToString(), message.Id.ToString(), "shoe size" })
                Assert.DoesNotContain(secret, e.Message);
        });
    }

    [Fact]
    public async Task A_waiting_email_is_dropped_once_the_event_is_revealed()
    {
        await AskAsync("What's your shoe size?");
        await app.WithDbAsync(async db =>
        {
            (await db.Events.SingleAsync(e => e.Id == ev)).Status = EventStatus.Revealed;
            await db.SaveChangesAsync();
        });

        At(11, 0);
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }
}
