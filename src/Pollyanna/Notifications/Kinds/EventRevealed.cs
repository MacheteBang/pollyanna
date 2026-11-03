using Pollyanna.Data;

namespace Pollyanna.Notifications.Kinds;

/// <summary>"Event is revealed" (spec §15): every participant and organizer, on <c>drawn → revealed</c>.</summary>
sealed class EventRevealed : NotificationKindDefinition<EventRevealed.Cause>
{
    /// <summary>The pairings were revealed, by <paramref name="Actor"/>.</summary>
    public sealed record Cause(Guid EventId, Guid? Actor) : NotificationCause(EventId, Actor);

    public override NotificationKind Kind => NotificationKind.EventRevealed;
    public override Audience Audience => Audience.ParticipantsAndOrganizers;

    public override NotificationContent Compose(NotificationContext context) => new(
        $"{context.Event.Name} is revealed",
        $"The pairings in \"{context.Event.Name}\" on Pollyanna are revealed: see who drew whom on the event page.",
        context.EventUrl);
}
