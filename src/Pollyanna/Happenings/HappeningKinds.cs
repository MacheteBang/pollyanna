using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Live;
using Pollyanna.Notifications;
using Pollyanna.Notifications.Kinds;
using Pollyanna.WhatsNew;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Happenings;

/// <summary>What a happening's email listener may use: the outbox, and the database as it stands in this transaction.</summary>
public sealed record EmailQueue(Outbox Outbox, PollyannaDb Db);

/// <summary>
/// One row of <see cref="HappeningKinds.Table"/>: what one kind of happening does.
/// </summary>
/// <param name="Live">Who its live hint goes to (spec §16 "Who a live signal goes to").</param>
/// <param name="Email">Queues the emails it causes (spec §15); null when it causes none.</param>
/// <param name="Stored">Kept in "What's new" as a stored row: who gets it, its line and link (spec §16); null when not.</param>
/// <param name="WhatsNew">Stored rows it deletes because what they say no longer holds; null when none.</param>
public sealed record HappeningKindRow(HappeningKind Kind, LiveTo Live, Func<Happening, EmailQueue, Task>? Email = null,
    StoredKind? Stored = null, Func<Happening, StoredNotifications, Task>? WhatsNew = null);

/// <summary>The per-kind table (spec §16): each <see cref="HappeningKind"/> once.</summary>
public static class HappeningKinds
{
    public static readonly IReadOnlyList<HappeningKindRow> Table =
    [
        new(HappeningKind.EventOpened, LiveTo.Event, Email: OpenedAsync,
            Stored: new(StoredFor.Participants, ParticipantsAsync, _ => "It's open: write your notes", v => v.EventUrl)),
        new(HappeningKind.NamesDrawn, LiveTo.Event,
            Email: (h, q) => q.Outbox.QueueAsync(new NamesDrawn.Cause(h.EventId, h.Actor)),
            Stored: new(StoredFor.Participants, ParticipantsAsync,
                v => v.Event.Redrawn ? "Names were drawn again: take another look" : "Names are drawn: see who you're giving to",
                v => v.EventUrl)),
        new(HappeningKind.EventRevealed, LiveTo.Event,
            Email: (h, q) => q.Outbox.QueueAsync(new EventRevealed.Cause(h.EventId, h.Actor)),
            Stored: new(StoredFor.Everyone, EveryoneAsync, _ => "See who drew whom", v => v.TakesPart ? v.EventUrl : v.ManageUrl),
            // The reveal clears every request to leave.
            WhatsNew: (h, s) => s.ForgetKindAsync(h.EventId, HappeningKind.RedrawNeeded)),
        new(HappeningKind.Invited, LiveTo.Organizers, Email: InvitedAsync),
        new(HappeningKind.AddedToEvent, LiveTo.Event,
            Email: (h, q) => q.Outbox.QueueAsync(new AddedToEvent.Cause(h.EventId, h.About!.Value, h.Actor)),
            // Not for a draft: "It's open" covers them when it opens, as the email does.
            Stored: new(StoredFor.Participants, AddedAsync,
                v => v.Event.Status == EventStatus.Open ? "You're taking part. Write your notes" : "You're taking part. See who you're giving to",
                v => v.EventUrl)),
        new(HappeningKind.InvitationAccepted, LiveTo.Event,
            Email: (h, q) => q.Outbox.QueueAsync(new AddedToEvent.Cause(h.EventId, h.About!.Value, Actor: null)),
            // Nobody acted, but whoever took it up is told nothing about themselves.
            Stored: new(StoredFor.Organizers, async (h, db) => [.. (await OrganizersAsync(h, db)).Where(p => p != h.About)],
                v => $"{v.Named ?? "Former participant"} is taking part", v => v.ManageUrl, h => h.About),
            // Whoever took it up is added to the event, as its email says.
            WhatsNew: (h, s) => s.WriteAsync(h with { Kind = HappeningKind.AddedToEvent }, Of(HappeningKind.AddedToEvent).Stored!)),
        new(HappeningKind.MadeOrganizer, LiveTo.Organizers,
            Email: (h, q) => q.Outbox.QueueAsync(new MadeOrganizer.Cause(h.EventId, h.About!.Value, h.Actor)),
            Stored: new(StoredFor.Organizers, (h, _) => Task.FromResult<IReadOnlyList<Guid>>([h.About!.Value]),
                v => $"{v.Named ?? "Someone"} made you an organizer", v => v.ManageUrl, h => h.Actor)),
        new(HappeningKind.RedrawNeeded, LiveTo.Organizers,
            Email: (h, q) => q.Outbox.QueueAsync(new RedrawNeeded.Cause(h.EventId, h.Actor!.Value)),
            Stored: new(StoredFor.Organizers, OrganizersAsync,
                v => $"{v.Named ?? "Former participant"} wants to leave, and that needs a redraw", v => v.ManageUrl, h => h.Actor)),
        // Nothing is ever queued for the giver (docs/adr/0001).
        new(HappeningKind.Question, LiveTo.Pair,
            Email: (h, q) => q.Outbox.QueueAsync(new Question.Cause(h.EventId, h.About!.Value, h.Actor))),
        // A redraw sends nothing, and a slip email still waiting is about a slip that's gone (spec §15).
        // Its "names are drawn" rows go for the same reason, and the redraw meets every request to leave.
        new(HappeningKind.NamesPutBack, LiveTo.Event, Email: (h, q) => NamesDrawn.WithdrawWaitingAsync(q.Db, h.EventId),
            WhatsNew: async (h, s) =>
            {
                await s.ForgetKindAsync(h.EventId, HappeningKind.NamesDrawn);
                await s.ForgetKindAsync(h.EventId, HappeningKind.RedrawNeeded);
            }),
        new(HappeningKind.EventArchived, LiveTo.Event, WhatsNew: (h, s) => s.ForgetEventAsync(h.EventId)),
        // Nobody is emailed, and its waiting emails are deleted (spec §4).
        new(HappeningKind.EventCanceled, LiveTo.Event,
            Email: (h, q) => q.Db.Notifications.Where(n => n.EventId == h.EventId).ExecuteDeleteAsync(),
            WhatsNew: (h, s) => s.ForgetEventAsync(h.EventId)),
        new(HappeningKind.DetailsChanged, LiveTo.Event),
        new(HappeningKind.NotesEdited, LiveTo.Event),
        new(HappeningKind.Left, LiveTo.Event, WhatsNew: OutAsync),
        new(HappeningKind.TakenOut, LiveTo.Event | LiveTo.About, WhatsNew: OutAsync),
        new(HappeningKind.Patched, LiveTo.Pair),
        new(HappeningKind.InvitationCanceled, LiveTo.Organizers),
        new(HappeningKind.OrganizerRemoved, LiveTo.Organizers | LiveTo.About, WhatsNew: (h, s) => s.ForgetRoleAsync(h.EventId, h.About!.Value, StoredFor.Organizers)),
        new(HappeningKind.RulesChanged, LiveTo.Organizers),
        new(HappeningKind.HasLooked, LiveTo.Organizers),
        new(HappeningKind.DoneShopping, LiveTo.Organizers),
        new(HappeningKind.LeaveWithdrawn, LiveTo.Organizers, WhatsNew: (h, s) => s.ForgetKindAsync(h.EventId, HappeningKind.RedrawNeeded, h.About)),
        new(HappeningKind.Reply, LiveTo.Pair),
        new(HappeningKind.ConversationClosed, LiveTo.Pair),
        new(HappeningKind.ConversationReopened, LiveTo.Pair),
    ];

    static async Task<IReadOnlyList<Guid>> ParticipantsAsync(Happening h, PollyannaDb db) =>
        await Audience.Participants.Members(db, h.EventId).ToListAsync();

    static async Task<IReadOnlyList<Guid>> OrganizersAsync(Happening h, PollyannaDb db) =>
        await Audience.Organizers.Members(db, h.EventId).ToListAsync();

    static async Task<IReadOnlyList<Guid>> EveryoneAsync(Happening h, PollyannaDb db) =>
        await Audience.ParticipantsAndOrganizers.Members(db, h.EventId).ToListAsync();

    /// <summary>The new participant, unless the event is still a draft.</summary>
    static async Task<IReadOnlyList<Guid>> AddedAsync(Happening h, PollyannaDb db)
    {
        var status = db.Events.Local.FirstOrDefault(e => e.Id == h.EventId)?.Status
            ?? await db.Events.Where(e => e.Id == h.EventId).Select(e => e.Status).SingleAsync();
        return EventLifecycle.ShownToParticipants.Contains(status) ? [h.About!.Value] : [];
    }

    /// <summary>
    /// Someone is out of the event as a participant: their rows for that role go, and any
    /// "wants to leave" row naming them, since their request is met.
    /// </summary>
    static async Task OutAsync(Happening h, StoredNotifications s)
    {
        await s.ForgetRoleAsync(h.EventId, h.About!.Value, StoredFor.Participants);
        await s.ForgetKindAsync(h.EventId, HappeningKind.RedrawNeeded, h.About);
    }

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
