using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Pollyanna.Notifications;

/// <summary>
/// Sends the outbox through the mail relay (spec §15). A row is deleted once sent; dropped unsent
/// if its person has deleted their account, switched emails off, has no usable address, no longer
/// has the role it was addressed to, or its kind no longer wants it; and dropped after 24 hours of
/// failed attempts.
/// Nothing records what was sent, and failures are logged by kind and SMTP error only
/// (docs/adr/0003).
/// </summary>
public sealed class OutboxSender(
    IServiceScopeFactory scopes,
    PollyannaSettings settings,
    IMailSender mail,
    TimeProvider clock,
    IOptions<OutboxSenderOptions> options,
    ILogger<OutboxSender> logger) : BackgroundService
{
    /// <summary>How often the outbox is looked at without a wake-up, for held and retried rows.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);
    static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);
    static readonly TimeSpan LongestRetry = TimeSpan.FromHours(1);

    readonly SemaphoreSlim running = new(1, 1);
    readonly SemaphoreSlim wake = new(0, 1);

    /// <summary>Asks for a run soon, e.g. right after notifications were saved.</summary>
    public void Wake()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.Mail is not { } relay)
            return;
        await CheckRelayAsync(relay, stoppingToken);
        if (!options.Value.SendInBackground)
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("The outbox run failed ({Error}); trying again shortly.", e.GetType().Name);
            }
            try
            {
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                await Task.WhenAny(wake.WaitAsync(poll.Token), Task.Delay(PollInterval, clock, poll.Token));
                poll.Cancel();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>A relay that can't be reached is only logged: the app starts, and rows wait to be retried (spec §11).</summary>
    async Task CheckRelayAsync(MailSettings relay, CancellationToken cancellationToken)
    {
        try
        {
            await mail.CheckAsync(cancellationToken);
            logger.LogInformation("Email notifications are on; the mail relay at {Host}:{Port} answered.", relay.Host, relay.Port);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "The mail relay at {Host}:{Port} can't be reached ({Error}). Pollyanna runs anyway; emails wait in the outbox and are retried.",
                relay.Host, relay.Port, SmtpMailSender.Describe(e));
        }
    }

    /// <summary>Sends or drops every row that is due now. Runs one at a time.</summary>
    public async Task SendDueAsync(CancellationToken cancellationToken)
    {
        if (settings.Mail is null)
            return;
        await running.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PollyannaDb>();
            var hat = scope.ServiceProvider.GetRequiredService<SealedHat>();
            var now = clock.GetUtcNow().UtcDateTime;
            var due = await db.Notifications.Where(n => n.NotBefore <= now).OrderBy(n => n.NotBefore)
                .Select(n => n.Id).ToListAsync(cancellationToken);
            foreach (var id in due)
            {
                // Read again for its turn: a change since the run began (a redraw, the switch
                // going off, a cancelled invitation) may have deleted it.
                if (await db.Notifications.SingleOrDefaultAsync(n => n.Id == id, cancellationToken) is not { } row)
                    continue;
                if (await PrepareAsync(db, hat, row, now, cancellationToken) is { } outgoing)
                {
                    try
                    {
                        await mail.SendAsync(outgoing, cancellationToken);
                    }
                    catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (GiveUpAfterFailure(row, e, now))
                            await DeleteAsync(db, row, cancellationToken);
                        else
                            await SaveRetryAsync(db, row, cancellationToken);
                        continue;
                    }
                }
                await DeleteAsync(db, row, cancellationToken);
            }
        }
        finally
        {
            running.Release();
        }
    }

    /// <summary>
    /// Deletes a sent or dropped row. One deleted meanwhile (say, switched off while its email was
    /// on the wire, which spec §15 accepts) is simply gone already.
    /// </summary>
    static async Task DeleteAsync(PollyannaDb db, Notification row, CancellationToken cancellationToken)
    {
        db.Entry(row).State = EntityState.Detached;
        await db.Notifications.Where(n => n.Id == row.Id).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Saves the next attempt's time, unless the row was deleted meanwhile.</summary>
    static async Task SaveRetryAsync(PollyannaDb db, Notification row, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>The email for <paramref name="row"/>, or null if the row is to be dropped unsent.</summary>
    async Task<OutgoingMail?> PrepareAsync(PollyannaDb db, SealedHat hat, Notification row, DateTime now, CancellationToken cancellationToken)
    {
        if (NotificationKindDefinition.For(row.Kind) is not { } kind)
            return null;
        var ev = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == row.EventId, cancellationToken);
        if (ev is null)
            return null;

        Person? person = null;
        string? address;
        if (kind.Audience.ByEmail())
            address = row.Email;
        else
        {
            person = await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.Id == row.PersonId, cancellationToken);
            // A deleted account leaves a tombstone without its sign-in or email.
            if (person is null || person.OidcSub is null)
                return null;
            // The email switch is read now, not when the row was queued (spec §15).
            if (!person.EmailsOn)
                return null;
            // The email from the last sign-in, unless the provider said it isn't verified (spec §15).
            address = person.EmailVerified == false ? null : person.Email;
        }
        if (string.IsNullOrWhiteSpace(address) || !await kind.Audience.StillHasAsync(db, row, cancellationToken))
            return null;

        var context = new NotificationContext
        {
            Row = row,
            Event = ev,
            Person = person,
            NamedPerson = row.NamedPersonId is { } named
                ? await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.Id == named, cancellationToken)
                : null,
            Db = db,
            Hat = hat,
            Now = now,
            PublicUrl = settings.PublicUrl,
        };
        if (!await kind.IsStillWantedAsync(context, cancellationToken))
            return null;
        var content = kind.Compose(context);
        return new OutgoingMail(address, content.Subject, Render(content, kind.Audience, ev, settings.PublicUrl));
    }

    /// <summary>
    /// The email text: what happened, the link, and a footer saying why it came and how to stop
    /// them (spec §15). An invitation's footer says to ignore it instead.
    /// </summary>
    public static string Render(NotificationContent content, Audience audience, Event ev, string publicUrl) =>
        $"{content.Body}\n\n{content.Link}\n\n-- \n{audience.Footer(ev, publicUrl)}\n";

    /// <summary>Records a failed attempt and schedules the next; true when it is time to give up.</summary>
    bool GiveUpAfterFailure(Notification row, Exception e, DateTime now)
    {
        row.FailedAttempts++;
        row.FirstFailedAt ??= now;
        if (now - row.FirstFailedAt >= GiveUpAfter)
        {
            logger.LogWarning("Gave up on a notification ({Kind}) after 24 hours of failures; the last was: {Error}.",
                row.Kind, SmtpMailSender.Describe(e));
            return true;
        }
        logger.LogWarning("Couldn't send a notification ({Kind}): {Error}. It will be retried.", row.Kind, SmtpMailSender.Describe(e));
        var backoff = FirstRetry * Math.Pow(2, Math.Min(row.FailedAttempts - 1, 10));
        row.NotBefore = now + (backoff < LongestRetry ? backoff : LongestRetry);
        return false;
    }
}

/// <summary>How <see cref="OutboxSender"/> runs.</summary>
public sealed class OutboxSenderOptions
{
    /// <summary>
    /// Whether the outbox is sent in the background, on wake-ups and polls. Always on in the app;
    /// tests turn it off and call <see cref="OutboxSender.SendDueAsync"/> themselves.
    /// </summary>
    public bool SendInBackground { get; set; } = true;
}
