using Pollyanna.Data;

namespace Pollyanna.Notifications.Kinds;

/// <summary>
/// "Invitation" (spec §15): an invited email address with no account yet, asked to sign in and
/// join. The row holds the address, and names who invited it. It is dropped unsent once the
/// invitation is no longer pending (an <see cref="Audience.Invitee"/> rule in <see cref="OutboxSender"/>).
/// </summary>
sealed class Invitation : NotificationKindDefinition<Invitation.Cause>
{
    /// <summary>
    /// <paramref name="Email"/> is to be emailed its invitation from <paramref name="InvitedBy"/>:
    /// <paramref name="Actor"/> invited it, or opened the draft it was invited to.
    /// </summary>
    public sealed record Cause(Guid EventId, string Email, Guid? InvitedBy, Guid? Actor) : NotificationCause(EventId, Actor);

    public override NotificationKind Kind => NotificationKind.Invitation;
    public override Audience Audience => Audience.Invitee;

    public override Task<IReadOnlyList<Recipient>> RecipientsAsync(Cause cause, PollyannaDb db) =>
        Task.FromResult<IReadOnlyList<Recipient>>([Recipient.Address(cause.Email)]);

    public override Guid? NamedPerson(Cause cause) => cause.InvitedBy;

    /// <summary>An address whose invitation email is still waiting (invited again after a cancel) gets nothing more.</summary>
    public override WaitingCover CoveredByWaiting => WaitingCover.SameRecipient;

    public override NotificationContent Compose(NotificationContext context)
    {
        // Who invited isn't secret: the manage page shows it. A deleted account has no name left.
        var inviter = context.NamedPerson?.DisplayName is { Length: > 0 } name ? name : "Someone";
        return new(
            $"You're invited to {context.Event.Name} on Pollyanna",
            $"{inviter} invited you to \"{context.Event.Name}\", a gift exchange on Pollyanna. "
            + "To join, sign in with this email address.",
            context.EventUrl);
    }
}
