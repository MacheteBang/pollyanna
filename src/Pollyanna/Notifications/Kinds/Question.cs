using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications.Kinds;

/// <summary>
/// "A question for you" (spec §15): the recipient of a thread whose giver asked something, held to
/// the next full hour (docs/adr/0004). The row holds the event and the recipient only, which the
/// assignment row already pairs; never the giver, the thread or the message.
/// </summary>
sealed class Question : NotificationKindDefinition<Question.Cause>
{
    /// <summary>
    /// <paramref name="Actor"/>, the giver, asked <paramref name="RecipientId"/> something. The
    /// giver isn't stored on the row (docs/adr/0001).
    /// </summary>
    public sealed record Cause(Guid EventId, Guid RecipientId, Guid? Actor) : NotificationCause(EventId, Actor);

    public override Task<IReadOnlyList<Recipient>> RecipientsAsync(Cause cause, PollyannaDb db) =>
        Task.FromResult<IReadOnlyList<Recipient>>([Recipient.Person(cause.RecipientId)]);

    /// <summary>The next full hour (docs/adr/0004).</summary>
    public override DateTime HoldUntil(DateTime now) => NextFullHour(now);

    /// <summary>One still waiting covers this question too (spec §15).</summary>
    public override WaitingCover CoveredByWaiting => WaitingCover.SameRecipient;

    public override NotificationKind Kind => NotificationKind.Question;
    public override Audience Audience => Audience.Participants;

    /// <summary>
    /// Dropped once the recipient has read every question (their read marker caught up), and once
    /// the thread no longer takes replies (the event left <c>drawn</c>).
    /// </summary>
    public override async Task<bool> IsStillWantedAsync(NotificationContext context, CancellationToken cancellationToken)
    {
        if (!EventLifecycle.Live.Contains(context.Event.Status))
            return false;
        return context.Row.PersonId is { } recipient
            && await context.Hat.HasUnreadQuestionAsync(context.Event.Id, recipient, cancellationToken);
    }

    public override NotificationContent Compose(NotificationContext context) => new(
        $"{context.Event.Name}: a question for you",
        $"Your giver in \"{context.Event.Name}\" asked you something. "
        + "Read it and reply on Pollyanna.",
        $"{context.EventUrl}/questions");

    /// <summary>The top of the next full hour after <paramref name="now"/> (UTC): when a question asked now is emailed.</summary>
    /// <remarks>The hour after the one the question is stored under (spec §15).</remarks>
    public static DateTime NextFullHour(DateTime now) => Message.HourOf(now).AddHours(1);
}
