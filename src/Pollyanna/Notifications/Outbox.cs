using Pollyanna.Configuration;
using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications;

/// <summary>
/// Adds notifications to be sent (spec §15). Rows are only added to the <see cref="PollyannaDb"/> context, so
/// they are saved by the caller's own <c>SaveChanges</c>, in the same transaction as the change
/// that causes them. With no mail relay configured, nothing is queued.
/// Callers only say what happened, as a kind's cause; the kind's rules decide the rest. Whoever
/// acted is always left out: nobody is emailed about their own action.
/// </summary>
public sealed class Outbox(PollyannaDb db, PollyannaSettings settings, TimeProvider clock, OutboxSender sender)
{
    bool subscribed;

    public bool Enabled => settings.Mail is not null;

    /// <summary>
    /// Adds the kind whose cause this is, by that kind's rules: for each of its recipients but
    /// the actor, unless the person has emails switched off or a waiting row already covers it.
    /// </summary>
    public async Task AddAsync<TCause>(TCause cause) where TCause : NotificationCause
    {
        if (!Enabled)
            return;
        var kind = NotificationKindDefinition.ForCause<TCause>();
        var named = kind.NamedPerson(cause);
        var notBefore = kind.HoldUntil(clock.GetUtcNow().UtcDateTime);
        foreach (var recipient in await kind.RecipientsAsync(cause, db))
        {
            if (recipient.PersonId is { } person && (person == cause.Actor
                    // Someone with emails switched off gets nothing caused while they're off, even if
                    // they switch back on before it would have been sent (spec §15). The sender checks again.
                    || !EmailsOn(person)))
                continue;
            if (await CoveredAsync(kind, cause.EventId, recipient, named))
                continue;
            db.Notifications.Add(new Notification
            {
                Kind = kind.Kind,
                EventId = cause.EventId,
                PersonId = recipient.PersonId,
                Email = recipient.Email,
                NamedPersonId = named,
                NotBefore = notBefore,
            });
            WakeSenderAfterSave();
        }
    }

    /// <summary>
    /// "Event is open" for the participants, and every invitation made while it was a draft,
    /// which is emailed now (spec §15).
    /// </summary>
    public async Task AddEventOpenAsync(Guid eventId, Guid? actor)
    {
        await AddAsync(new Kinds.EventOpen.Cause(eventId, actor));
        var invitations = await db.Invitations.Where(i => i.EventId == eventId).OrderBy(i => i.CreatedAt).ToListAsync();
        foreach (var invitation in invitations)
            await AddAsync(new Kinds.Invitation.Cause(eventId, invitation.Email, invitation.InvitedBy, actor));
    }

    /// <summary>An invitation's email, unless the event is a draft: a draft's invitations are emailed when it opens (spec §4, §15).</summary>
    public async Task AddInvitationAsync(Guid eventId, Guid? invitationId, Guid? actor)
    {
        var ev = await db.Events.FindAsync(eventId);
        if (ev is null || !Events.EventLifecycle.ShownToParticipants.Contains(ev.Status))
            return;
        if (await db.Invitations.FindAsync(invitationId) is { } invitation)
            await AddAsync(new Kinds.Invitation.Cause(eventId, invitation.Email, invitation.InvitedBy, actor));
    }

    /// <summary>Every email of the event still waiting goes, unsent: it was canceled (spec §4).</summary>
    public Task WithdrawEventAsync(Guid eventId) =>
        db.Notifications.Where(n => n.EventId == eventId).ExecuteDeleteAsync();

    /// <summary>The "names are drawn" emails still waiting go: the slips they point to are gone (a redraw, spec §15).</summary>
    public Task WithdrawNamesDrawnAsync(Guid eventId) => Kinds.NamesDrawn.WithdrawWaitingAsync(db, eventId);

    /// <summary>Whether a waiting row covers a new one for <paramref name="recipient"/>, as <paramref name="kind"/> says.</summary>
    async Task<bool> CoveredAsync(NotificationKindDefinition kind, Guid eventId, Recipient recipient, Guid? named)
    {
        if (kind.CoveredByWaiting == WaitingCover.Nothing)
            return false;
        var sameNamed = kind.CoveredByWaiting == WaitingCover.SameRecipientAndNamedPerson;
        var waiting = db.Notifications.Where(n => n.Kind == kind.Kind && n.EventId == eventId
            && (!sameNamed || n.NamedPersonId == named));
        if (recipient.PersonId is { } person)
            return await waiting.AnyAsync(n => n.PersonId == person);
        var address = Person.NormalizeEmail(recipient.Email!);
        var emails = await waiting.Where(n => n.Email != null).Select(n => n.Email!).ToListAsync();
        return emails.Any(e => Person.NormalizeEmail(e) == address);
    }

    /// <summary>The person's email switch, as this context stands; without tracking anything new.</summary>
    bool EmailsOn(Guid personId) =>
        db.People.Local.FirstOrDefault(p => p.Id == personId)?.EmailsOn
        ?? db.People.AsNoTracking().Where(p => p.Id == personId).Select(p => (bool?)p.EmailsOn).SingleOrDefault()
        ?? true;

    /// <summary>Lets the background sender start as soon as the rows are saved, rather than at its next poll.</summary>
    void WakeSenderAfterSave()
    {
        if (subscribed)
            return;
        subscribed = true;
        db.SavedChanges += (_, _) => sender.Wake();
    }
}
