using Pollyanna.Data;

namespace Pollyanna.Notifications.Kinds;

/// <summary>
/// "Made an organizer" (spec §15): the new organizer, naming who made them one (the row's
/// <see cref="Data.Notification.NamedPersonId"/>), which the manage page shows anyway.
/// </summary>
sealed class MadeOrganizer : NotificationKindDefinition<MadeOrganizer.Cause>
{
    /// <summary><paramref name="Actor"/> made <paramref name="Person"/> an organizer.</summary>
    public sealed record Cause(Guid EventId, Guid Person, Guid? Actor) : NotificationCause(EventId, Actor);

    public override NotificationKind Kind => NotificationKind.MadeOrganizer;
    public override Audience Audience => Audience.Organizers;

    public override Task<IReadOnlyList<Recipient>> RecipientsAsync(Cause cause, PollyannaDb db) =>
        Task.FromResult<IReadOnlyList<Recipient>>([Recipient.Person(cause.Person)]);

    public override Guid? NamedPerson(Cause cause) => cause.Actor;

    public override NotificationContent Compose(NotificationContext context) => new(
        $"{context.NamedPersonName} made you an organizer of {context.Event.Name}",
        $"{context.NamedPersonName} made you an organizer of \"{context.Event.Name}\" on Pollyanna. "
        + "You can now manage its people, the draw and its details.",
        $"{context.EventUrl}/manage");
}
