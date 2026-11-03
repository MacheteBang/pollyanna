using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications.Kinds;

/// <summary>
/// "Added to an event" (spec §15): the new participant, added by an organizer or by an invitation
/// accepted at sign-in. Not for a draft (the "open" email covers it), and never says anything
/// about anyone's assignment.
/// </summary>
sealed class AddedToEvent : NotificationKindDefinition<AddedToEvent.Cause>
{
    /// <summary><paramref name="Person"/> joined, added by <paramref name="Actor"/> or (null) by an invitation accepted at sign-in.</summary>
    public sealed record Cause(Guid EventId, Guid Person, Guid? Actor) : NotificationCause(EventId, Actor);

    public override NotificationKind Kind => NotificationKind.AddedToEvent;
    public override Audience Audience => Audience.Participants;

    /// <summary>The new participant, unless the event is still a draft.</summary>
    public override async Task<IReadOnlyList<Recipient>> RecipientsAsync(Cause cause, PollyannaDb db)
    {
        var status = db.Events.Local.FirstOrDefault(e => e.Id == cause.EventId)?.Status
            ?? await db.Events.Where(e => e.Id == cause.EventId).Select(e => e.Status).SingleAsync();
        return EventLifecycle.ShownToParticipants.Contains(status) ? [Recipient.Person(cause.Person)] : [];
    }

    public override NotificationContent Compose(NotificationContext context) => new(
        $"You've been added to {context.Event.Name}",
        $"You've been added to \"{context.Event.Name}\" on Pollyanna. " + (context.Event.Status == EventStatus.Open
            ? "Write your notes so your giver knows what to get you: likes, sizes, no-gos."
            : "Open it to see your slip and write your notes."),
        context.EventUrl);
}
