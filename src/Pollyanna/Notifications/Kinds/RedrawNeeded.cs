using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications.Kinds;

/// <summary>
/// "Redraw needed" (spec §15, §4 "Leaving"): every organizer, when a participant's request to leave
/// a <c>drawn</c> event can only be met by a redraw. Names the leaver, as the manage page does.
/// </summary>
sealed class RedrawNeeded : NotificationKindDefinition<RedrawNeeded.Cause>
{
    /// <summary>
    /// <paramref name="Leaver"/> asked to leave, by leaving or by deleting their account. They are
    /// the actor, so an organizer who is the leaver isn't emailed about their own request.
    /// </summary>
    public sealed record Cause(Guid EventId, Guid Leaver) : NotificationCause(EventId, Leaver);

    public override Guid? NamedPerson(Cause cause) => cause.Leaver;

    /// <summary>An organizer with one still waiting about the same leaver (asked, stayed, asked again) gets no second one.</summary>
    public override WaitingCover CoveredByWaiting => WaitingCover.SameRecipientAndNamedPerson;

    public override NotificationKind Kind => NotificationKind.RedrawNeeded;
    public override Audience Audience => Audience.Organizers;

    /// <summary>
    /// Dropped once the request is no longer waiting: withdrawn ("Stay in after all"), or met or
    /// cleared by a redraw or the reveal, which also take the leaver out or clear the request.
    /// </summary>
    public override Task<bool> IsStillWantedAsync(NotificationContext context, CancellationToken cancellationToken) =>
        context.Db.Participants.AnyAsync(p => p.EventId == context.Row.EventId
            && p.PersonId == context.Row.NamedPersonId && p.WantsToLeave, cancellationToken);

    public override NotificationContent Compose(NotificationContext context) => new(
        $"{context.Event.Name}: a redraw is needed",
        $"{context.NamedPersonName} wants to leave \"{context.Event.Name}\"; a redraw is needed. "
        + "Taking them out means drawing names again, which you can do from the manage page.",
        $"{context.EventUrl}/manage");
}
