using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Notifications;
using Pollyanna.Notifications.Kinds;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Happenings;

/// <summary>What a happening's email listener may use: the outbox, and the database as it stands in this transaction.</summary>
public sealed record EmailQueue(Outbox Outbox, PollyannaDb Db);

/// <summary>
/// One row of <see cref="HappeningKinds.Table"/>: what one kind of happening does.
/// Later columns say whether it is stored or worked out in "What's new", and how it travels live (spec §16).
/// </summary>
/// <param name="Email">Queues the emails it causes (spec §15); null when it causes none.</param>
public sealed record HappeningKindRow(HappeningKind Kind, Func<Happening, EmailQueue, Task>? Email = null);

/// <summary>The per-kind table (spec §16): each <see cref="HappeningKind"/> once.</summary>
public static class HappeningKinds
{
    public static readonly IReadOnlyList<HappeningKindRow> Table =
    [
        new(HappeningKind.EventOpened, Email: OpenedAsync),
        new(HappeningKind.NamesDrawn,
            Email: (h, q) => q.Outbox.QueueAsync(new NamesDrawn.Cause(h.EventId, h.Actor))),
        new(HappeningKind.EventRevealed,
            Email: (h, q) => q.Outbox.QueueAsync(new EventRevealed.Cause(h.EventId, h.Actor))),
        new(HappeningKind.Invited, Email: InvitedAsync),
        new(HappeningKind.AddedToEvent,
            Email: (h, q) => q.Outbox.QueueAsync(new AddedToEvent.Cause(h.EventId, h.About!.Value, h.Actor))),
        new(HappeningKind.InvitationAccepted,
            Email: (h, q) => q.Outbox.QueueAsync(new AddedToEvent.Cause(h.EventId, h.About!.Value, Actor: null))),
        new(HappeningKind.MadeOrganizer,
            Email: (h, q) => q.Outbox.QueueAsync(new MadeOrganizer.Cause(h.EventId, h.About!.Value, h.Actor))),
        new(HappeningKind.RedrawNeeded,
            Email: (h, q) => q.Outbox.QueueAsync(new RedrawNeeded.Cause(h.EventId, h.Actor!.Value))),
        // Nothing is ever queued for the giver (docs/adr/0001).
        new(HappeningKind.Question,
            Email: (h, q) => q.Outbox.QueueAsync(new Question.Cause(h.EventId, h.About!.Value, h.Actor))),
        // A redraw sends nothing, and a slip email still waiting is about a slip that's gone (spec §15).
        new(HappeningKind.NamesPutBack, Email: (h, q) => NamesDrawn.WithdrawWaitingAsync(q.Db, h.EventId)),
        new(HappeningKind.EventArchived),
        // Nobody is emailed, and its waiting emails are deleted (spec §4).
        new(HappeningKind.EventCanceled,
            Email: (h, q) => q.Db.Notifications.Where(n => n.EventId == h.EventId).ExecuteDeleteAsync()),
        new(HappeningKind.DetailsChanged),
        new(HappeningKind.NotesEdited),
        new(HappeningKind.Left),
        new(HappeningKind.TakenOut),
        new(HappeningKind.Patched),
        new(HappeningKind.InvitationCanceled),
        new(HappeningKind.OrganizerRemoved),
        new(HappeningKind.RulesChanged),
        new(HappeningKind.HasLooked),
        new(HappeningKind.DoneShopping),
        new(HappeningKind.LeaveWithdrawn),
        new(HappeningKind.Reply),
        new(HappeningKind.ConversationClosed),
        new(HappeningKind.ConversationReopened),
    ];

    /// <summary>"Event is open" for the participants, and every invitation made while it was a draft (spec §15).</summary>
    static async Task OpenedAsync(Happening h, EmailQueue q)
    {
        await q.Outbox.QueueAsync(new EventOpen.Cause(h.EventId, h.Actor));
        var invitations = await q.Db.Invitations.Where(i => i.EventId == h.EventId).OrderBy(i => i.CreatedAt).ToListAsync();
        foreach (var invitation in invitations)
            await q.Outbox.QueueAsync(new Notifications.Kinds.Invitation.Cause(h.EventId, invitation.Email, invitation.InvitedBy, h.Actor));
    }

    /// <summary>The invitation's email, unless the event is a draft: its invitations are emailed when it opens (spec §4, §15).</summary>
    static async Task InvitedAsync(Happening h, EmailQueue q)
    {
        var ev = await q.Db.Events.FindAsync(h.EventId);
        if (ev is null || !EventLifecycle.ShownToParticipants.Contains(ev.Status))
            return;
        if (await q.Db.Invitations.FindAsync(h.About) is { } invitation)
            await q.Outbox.QueueAsync(new Notifications.Kinds.Invitation.Cause(h.EventId, invitation.Email, invitation.InvitedBy, h.Actor));
    }

    static readonly Dictionary<HappeningKind, HappeningKindRow> ByKind = Table.ToDictionary(r => r.Kind);

    /// <summary>The row of <paramref name="kind"/>; throws if the table has none.</summary>
    public static HappeningKindRow Of(HappeningKind kind) =>
        ByKind.GetValueOrDefault(kind) ?? throw new InvalidOperationException($"{kind} has no row in the happening table.");
}
