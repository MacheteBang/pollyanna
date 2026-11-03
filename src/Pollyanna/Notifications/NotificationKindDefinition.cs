using System.Reflection;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications;

/// <summary>What one email says: the subject, what happened, and the page to open.</summary>
/// <param name="Body">Says what happened and names the event. Never a message body, notes, or a giver's or recipient's name.</param>
/// <param name="Link">An absolute URL in Pollyanna, usually <see cref="NotificationContext.EventUrl"/>.</param>
public sealed record NotificationContent(string Subject, string Body, string Link);

/// <summary>Everything a kind may read when its notification is about to be sent.</summary>
public sealed class NotificationContext
{
    public required Notification Row { get; init; }
    public required Event Event { get; init; }
    /// <summary>The person it goes to; null for an invitation.</summary>
    public required Person? Person { get; init; }
    /// <summary>The row's <see cref="Notification.NamedPersonId"/>, if any.</summary>
    public required Person? NamedPerson { get; init; }
    public required PollyannaDb Db { get; init; }
    /// <summary>The sealed draw, over <see cref="Db"/>.</summary>
    public required SealedHat Hat { get; init; }
    public required DateTime Now { get; init; }
    /// <summary>The public URL, without a trailing slash.</summary>
    public required string PublicUrl { get; init; }

    public string EventUrl => $"{PublicUrl}/events/{Event.Id}";
    /// <summary>How the email names <see cref="NamedPerson"/>, as the pages do.</summary>
    public string NamedPersonName => NamedPerson?.DisplayName ?? "Former participant";
}

/// <summary>
/// Something that happened, as one kind's own record (e.g. <c>RedrawNeeded.Cause</c>), handed to
/// <see cref="Outbox.QueueAsync"/>. Holds IDs only; a kind reads anything else it needs.
/// </summary>
/// <param name="Actor">Who did it; never emailed about it (spec §15). Null when nobody did, as at sign-in.</param>
public abstract record NotificationCause(Guid EventId, Guid? Actor);

/// <summary>Who a row goes to: a person, or (for an invitation) an email address.</summary>
public readonly record struct Recipient(Guid? PersonId, string? Email)
{
    public static Recipient Person(Guid personId) => new(personId, null);
    public static Recipient Address(string email) => new(null, email);
}

/// <summary>Which row of the same kind and event, still waiting, covers a new one so that it isn't queued (spec §15).</summary>
public enum WaitingCover
{
    /// <summary>Every cause queues its own row.</summary>
    Nothing,
    /// <summary>A waiting row for the same recipient (the same address, ignoring case, for an invitation).</summary>
    SameRecipient,
    /// <summary>A waiting row for the same recipient that names the same person.</summary>
    SameRecipientAndNamedPerson,
}

/// <summary>
/// One notification kind: who it is for, when a waiting row is no longer wanted, and what the
/// email says. Subclasses are found by reflection, so a new kind is one new file in
/// <c>Notifications/Kinds/</c> plus the <see cref="Outbox.QueueAsync"/> call where its cause happens.
/// The shared rules (account deleted, no usable address, role lost) are applied by
/// <see cref="OutboxSender"/> before <see cref="IsStillWantedAsync"/>; the footer is added there too.
/// </summary>
public abstract class NotificationKindDefinition
{
    public abstract NotificationKind Kind { get; }

    /// <summary>
    /// The role the email is addressed to, which the person must still have when it is sent, and
    /// what its footer says. Unless the kind says otherwise, it is queued for everyone in it.
    /// </summary>
    public abstract Audience Audience { get; }

    /// <summary>Whether a waiting row covers a new one, so the new one isn't queued.</summary>
    public virtual WaitingCover CoveredByWaiting => WaitingCover.Nothing;

    /// <summary>A kind's own reasons to drop a waiting row unsent, checked at send time.</summary>
    public virtual Task<bool> IsStillWantedAsync(NotificationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public abstract NotificationContent Compose(NotificationContext context);

    static readonly NotificationKindDefinition[] Definitions = typeof(NotificationKindDefinition).Assembly
        .GetTypes()
        .Where(t => t is { IsAbstract: false, IsClass: true } && t.IsSubclassOf(typeof(NotificationKindDefinition)))
        .Select(t => (NotificationKindDefinition)Activator.CreateInstance(t)!)
        .ToArray();

    static readonly Dictionary<NotificationKind, NotificationKindDefinition> All = Definitions.ToDictionary(d => d.Kind);

    static readonly Dictionary<Type, NotificationKindDefinition> ByCause = Definitions
        .Where(d => d.GetType().BaseType is { IsGenericType: true } b && b.GetGenericTypeDefinition() == typeof(NotificationKindDefinition<>))
        .ToDictionary(d => d.GetType().BaseType!.GetGenericArguments()[0]);

    /// <summary>The definition of <paramref name="kind"/>, or null if it has none yet.</summary>
    public static NotificationKindDefinition? For(NotificationKind kind) => All.GetValueOrDefault(kind);

    /// <summary>The kind whose cause is <typeparamref name="TCause"/>.</summary>
    public static NotificationKindDefinition<TCause> ForCause<TCause>() where TCause : NotificationCause =>
        (NotificationKindDefinition<TCause>?)ByCause.GetValueOrDefault(typeof(TCause))
        ?? throw new InvalidOperationException($"{typeof(TCause)} is no notification kind's cause.");
}

/// <summary>
/// A kind queued by its own <typeparamref name="TCause"/>: the kind answers who gets it, whom it
/// names and how long it is held, and <see cref="Outbox.QueueAsync"/> does the rest.
/// </summary>
public abstract class NotificationKindDefinition<TCause> : NotificationKindDefinition where TCause : NotificationCause
{
    /// <summary>
    /// Who it is queued for, as the database stands in this transaction, before the actor is left
    /// out. Everyone in <see cref="NotificationKindDefinition.Audience"/> unless the kind says otherwise.
    /// </summary>
    public virtual async Task<IReadOnlyList<Recipient>> RecipientsAsync(TCause cause, PollyannaDb db) =>
        (await Audience.Members(db, cause.EventId).ToListAsync()).Select(Recipient.Person).ToList();

    /// <summary>Someone the email names (who acted, or who it is about), stored on the row.</summary>
    public virtual Guid? NamedPerson(TCause cause) => null;

    /// <summary>Not to be sent before this (UTC). Straight away unless the kind holds it.</summary>
    public virtual DateTime HoldUntil(DateTime now) => now;
}
