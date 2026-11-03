using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>
/// Where the signed-in person stands in one event, for the visibility rules (spec §4): their role,
/// combined with what the event's status allows (<see cref="EventLifecycle"/>).
/// </summary>
public sealed record Membership(Event Event, bool IsOrganizer, bool IsParticipant)
{
    /// <summary>
    /// Organizers always see their event; participants see it once it is out of draft. Everyone
    /// else gets "not found", so an event's existence isn't revealed either.
    /// </summary>
    public bool CanView => IsOrganizer || (IsParticipant && ShownToParticipants);

    /// <summary>Out of draft, so participants see it and invitations are emailed.</summary>
    public bool ShownToParticipants => EventLifecycle.ShownToParticipants.Contains(Event.Status);

    /// <summary>Participants, invitations, households and exclusions change only before the draw.</summary>
    public bool PeopleEditable => EventLifecycle.BeforeDraw.Contains(Event.Status);

    /// <summary>People can be added and taken out until the reveal: after the draw, as late joiners and dropouts.</summary>
    public bool PeopleChangeable => EventLifecycle.BeforeReveal.Contains(Event.Status);

    /// <summary>Details, organizers and notes are read-only once the event is archived.</summary>
    public bool Editable => EventLifecycle.Active.Contains(Event.Status);

    /// <summary>Names are drawn, whether or not the exchange has happened.</summary>
    public bool NamesDrawn => EventLifecycle.NamesDrawn.Contains(Event.Status);

    /// <summary>Drawn and not yet revealed: slips carry markers and threads take messages.</summary>
    public bool IsLive => EventLifecycle.Live.Contains(Event.Status);

    /// <summary>Revealed or archived: the exchange has happened.</summary>
    public bool IsOver => EventLifecycle.Over.Contains(Event.Status);

    /// <summary>
    /// A participant can leave on their own until the event is revealed (spec §4); after the
    /// draw it takes a second confirmation. Once revealed, the exchange has happened.
    /// </summary>
    public bool CanLeave => IsParticipant && CanView && PeopleChangeable;

    /// <summary>
    /// Question threads can be read once names are drawn, and stay readable (read-only) after
    /// reveal and, if their messages were kept, after archiving (spec §6). Only their own two
    /// people read them.
    /// </summary>
    public bool ThreadsReadable => IsParticipant && NamesDrawn;

    /// <summary>The event's status allows <paramref name="transition"/>; the page has checked the role.</summary>
    public bool Allows(Transition transition) => transition.Refusal(Event.Status) is null;
}

public static class EventAccess
{
    public static async Task<Membership?> FindAsync(PollyannaDb db, Guid eventId, Guid me, CancellationToken cancellationToken = default)
    {
        var row = await db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new
            {
                Event = e,
                IsOrganizer = e.Organizers.Any(o => o.PersonId == me),
                IsParticipant = e.Participants.Any(p => p.PersonId == me),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new Membership(row.Event, row.IsOrganizer, row.IsParticipant);
    }
}
