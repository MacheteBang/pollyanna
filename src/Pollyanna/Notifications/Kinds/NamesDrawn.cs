using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications.Kinds;

/// <summary>
/// "Names are drawn" (spec §15): every participant, after a draw. It never says whose slip it is
/// (ADR-0001). After a redraw's new draw it says the names were drawn again. A redraw deletes
/// any still waiting, so nobody is told about a slip that is gone.
/// </summary>
sealed class NamesDrawn : NotificationKindDefinition<NamesDrawn.Cause>
{
    /// <summary>Names were drawn, by <paramref name="Actor"/>.</summary>
    public sealed record Cause(Guid EventId, Guid? Actor) : NotificationCause(EventId, Actor);

    public override NotificationKind Kind => NotificationKind.NamesDrawn;
    public override Audience Audience => Audience.Participants;

    /// <summary>
    /// Deletes every waiting one for the event, at a redraw: a redraw sends nothing (spec §15), and
    /// a slip email still waiting is about a slip that's gone. Part of the redraw's transaction.
    /// </summary>
    public static Task WithdrawWaitingAsync(PollyannaDb db, Guid eventId) =>
        db.Notifications.Where(n => n.EventId == eventId && n.Kind == NotificationKind.NamesDrawn).ExecuteDeleteAsync();

    public override NotificationContent Compose(NotificationContext context) => context.Event.Redrawn
        ? new(
            $"{context.Event.Name}: names were drawn again",
            $"Names were drawn again, take another look: your slip in \"{context.Event.Name}\" on Pollyanna "
            + "is ready, and it may not be the one you saw before.",
            context.EventUrl)
        : new(
            $"{context.Event.Name}: names are drawn",
            $"Names are drawn in \"{context.Event.Name}\" on Pollyanna, and your slip is ready. "
            + "Open the event to see who you're giving to.",
            context.EventUrl);
}
