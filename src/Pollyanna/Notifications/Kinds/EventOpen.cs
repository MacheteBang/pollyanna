using Pollyanna.Data;

namespace Pollyanna.Notifications.Kinds;

/// <summary>"Event is open" (spec §15): every participant, on <c>draft → open</c>.</summary>
sealed class EventOpen : NotificationKindDefinition<EventOpen.Cause>
{
    /// <summary>The draft was opened, by <paramref name="Actor"/>.</summary>
    public sealed record Cause(Guid EventId, Guid? Actor) : NotificationCause(EventId, Actor);

    public override NotificationKind Kind => NotificationKind.EventOpen;
    public override Audience Audience => Audience.Participants;

    public override NotificationContent Compose(NotificationContext context) => new(
        $"{context.Event.Name} is open: write your notes",
        $"\"{context.Event.Name}\" is open on Pollyanna. Write your notes so your giver knows "
        + "what to get you: likes, sizes, no-gos.",
        context.EventUrl);
}
