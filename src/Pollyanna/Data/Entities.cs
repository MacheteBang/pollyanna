namespace Pollyanna.Data;

/// <summary>
/// A person (spec §4). Identified by the provider's issuer + <c>sub</c>, mapped to a Pollyanna ID
/// that everything else uses. A deleted account leaves a tombstone: the row with only its ID.
/// </summary>
public sealed class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? OidcIssuer { get; set; }
    public string? OidcSub { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    /// <summary><see cref="Email"/> lower-cased for case-insensitive matching.</summary>
    public string? EmailNormalized { get; set; }
    /// <summary>The provider's <c>email_verified</c> claim; null when it didn't send one.</summary>
    public bool? EmailVerified { get; set; }
    public string? PictureUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>The email switch, "Email me about Pollyanna events" (spec §15); on by default, read when a notification is sent.</summary>
    public bool EmailsOn { get; set; } = true;

    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}

/// <summary>A profile picture downloaded from the provider's <c>picture</c> URL (spec §3).</summary>
public sealed class PersonPicture
{
    public Guid PersonId { get; set; }
    public required byte[] Data { get; set; }
    public required string ContentType { get; set; }
}

/// <summary>
/// A server-side session (spec §3). The cookie holds a random 256-bit token; only its SHA-256
/// hash is stored, so the database alone can't be used to impersonate anyone.
/// </summary>
public sealed class Session
{
    /// <summary>Lower-case hex SHA-256 of the cookie token.</summary>
    public required string Id { get; set; }
    public Guid PersonId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public enum EventStatus
{
    Draft,
    Open,
    Drawn,
    Revealed,
    Archived,
}

/// <summary>An exchange (spec §4).</summary>
public sealed class Event
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string BudgetText { get; set; } = "";
    public DateOnly ExchangeDate { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Draft;
    public Guid? PreviousEventId { get; set; }
    public int NoRepeatDepth { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Names went back in the hat at least once, so new cards say "Names were drawn again".</summary>
    public bool Redrawn { get; set; }
    /// <summary>
    /// The full pairing list was revealed (spec §4). Stays set once archived, so an event archived
    /// straight from drawn keeps its pairings sealed for good.
    /// </summary>
    public bool PairingsRevealed { get; set; }

    /// <summary>Taken from the exchange date, never entered separately.</summary>
    public int Year => ExchangeDate.Year;

    public List<Organizer> Organizers { get; set; } = [];
    public List<Participant> Participants { get; set; } = [];
    public List<Invitation> Invitations { get; set; } = [];
    public List<Household> Households { get; set; } = [];
    public List<Exclusion> Exclusions { get; set; } = [];
    public List<Assignment> Assignments { get; set; } = [];
}

public sealed class Organizer
{
    public Guid EventId { get; set; }
    public Guid PersonId { get; set; }
}

public sealed class Participant
{
    public Guid EventId { get; set; }
    public Guid PersonId { get; set; }
    public string Notes { get; set; } = "";
    public int NotesVersion { get; set; }
    public bool DoneShopping { get; set; }
    public DateTime JoinedAt { get; set; }
    /// <summary>At most one household per event (spec §4).</summary>
    public Guid? HouseholdId { get; set; }
    /// <summary>
    /// Asked to leave after the draw when the patch wasn't possible (spec §4): still in, and taken
    /// out by the next redraw.
    /// </summary>
    public bool WantsToLeave { get; set; }
}

/// <summary>
/// An email invitation to an event (spec §4). It turns into a participant when someone signs in
/// with a matching email, and is deleted then.
/// </summary>
public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    /// <summary>The email as the organizer typed it.</summary>
    public required string Email { get; set; }
    /// <summary><see cref="Email"/> lower-cased for case-insensitive matching.</summary>
    public required string EmailNormalized { get; set; }
    public Guid InvitedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>An event-scoped named group whose members never draw each other (spec §4).</summary>
public sealed class Household
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public required string Name { get; set; }
}

/// <summary>
/// An extra "these two never draw each other" rule, always symmetric (spec §4). Stored once per
/// pair, with <see cref="PersonA"/> the smaller ID.
/// </summary>
public sealed class Exclusion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid PersonA { get; set; }
    public Guid PersonB { get; set; }

    public static Exclusion Between(Guid eventId, Guid a, Guid b) =>
        a.CompareTo(b) < 0
            ? new Exclusion { EventId = eventId, PersonA = a, PersonB = b }
            : new Exclusion { EventId = eventId, PersonA = b, PersonB = a };
}

/// <summary>
/// One giver's slip in a drawn event, sealed (spec §5.2): the recipient in plaintext, the giver
/// only as an HMAC tag under the draw key. No column names the giver and none holds a time.
/// </summary>
public sealed class Assignment
{
    /// <summary>Random, so the ID says nothing about when or in which order rows were made.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid RecipientId { get; set; }
    /// <summary><c>HMAC-SHA256(draw_key, "giver" ‖ event_id ‖ giver_id)</c>, see <c>DrawKey.GiverTag</c>.</summary>
    public required byte[] GiverTag { get; set; }
    /// <summary>The giver has opened the reveal card at least once ("viewed assignment").</summary>
    public bool Viewed { get; set; }
    /// <summary>The recipient's notes version when the giver last opened the card: a counter, not a time.</summary>
    public int NotesVersionSeen { get; set; }
    /// <summary>A patch gave this giver a new recipient; the notice shows until the card is opened.</summary>
    public bool RecipientChanged { get; set; }
    /// <summary>The recipient closed the question thread (spec §6): nobody can post until they reopen it.</summary>
    public bool ThreadClosed { get; set; }
    /// <summary>The <see cref="Message.Seq"/> of the last message the giver has seen.</summary>
    public int GiverReadUpto { get; set; }
    /// <summary>The <see cref="Message.Seq"/> of the last message the recipient has seen.</summary>
    public int RecipientReadUpto { get; set; }
}

/// <summary>Which side of a question thread wrote a message: a role, never a person.</summary>
public enum MessageSender
{
    Giver,
    Recipient,
}

/// <summary>
/// One passed note in an assignment's anonymous question thread (spec §6). It names a role, not
/// a person; <see cref="Seq"/> orders the thread and counts only within it, and the time is
/// rounded down to the hour.
/// </summary>
public sealed class Message
{
    /// <summary>Random, like the assignment's.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    /// <summary>1, 2, 3… within the thread.</summary>
    public int Seq { get; set; }
    public MessageSender Sender { get; set; }
    public required string Body { get; set; }
    /// <summary>UTC, rounded down to the hour (<see cref="HourOf"/>).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>The hour a message posted at <paramref name="now"/> (UTC) is stored under, to blunt timing guesses (spec §6).</summary>
    public static DateTime HourOf(DateTime now) =>
        new(now.Ticks - now.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
}

/// <summary>
/// One notification waiting in the outbox (spec §15). It is written in the same transaction as
/// the change that causes it and deleted once sent or dropped: nothing records sent mail
/// (docs/adr/0003). The address, the person's role and the content are worked out when it is
/// sent, not here. No column holds when it was queued: for question emails that would give
/// back the minute the giver posted (docs/adr/0004).
/// </summary>
public sealed class Notification
{
    /// <summary>Random, like an assignment's.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public NotificationKind Kind { get; set; }
    public Guid EventId { get; set; }
    /// <summary>Who it is for; null only for an invitation, which has <see cref="Email"/> instead.</summary>
    public Guid? PersonId { get; set; }
    /// <summary>The invited address, for an invitation (no person yet).</summary>
    public string? Email { get; set; }
    /// <summary>
    /// Someone the email names, read when it is sent: who acted ("Alice made you an organizer")
    /// or who it is about ("Carol wants to leave"). Never a giver.
    /// </summary>
    public Guid? NamedPersonId { get; set; }
    /// <summary>
    /// Not to be sent before this (UTC): the next full hour for question emails, otherwise when it
    /// was queued; pushed back after each failed attempt.
    /// </summary>
    public DateTime NotBefore { get; set; }
    public int FailedAttempts { get; set; }
    /// <summary>The first failed attempt; the row is dropped 24 hours after it.</summary>
    public DateTime? FirstFailedAt { get; set; }
}

/// <summary>
/// The fixed set of things a notification is about (spec §15, "What is emailed"). Stored by
/// name, so the order here doesn't matter. Each kind's rules (who gets it, holding, covering,
/// dropping) and content live in its own <see cref="Pollyanna.Notifications.NotificationKindDefinition"/>
/// under <c>Notifications/Kinds/</c>.
/// </summary>
public enum NotificationKind
{
    /// <summary><c>draft → open</c>: every participant, "write your notes".</summary>
    EventOpen,
    /// <summary>A new participant, added directly or by an accepted invitation.</summary>
    AddedToEvent,
    /// <summary>An invited email address with no account yet (the row holds the email).</summary>
    Invitation,
    /// <summary>A new organizer, naming who made them one.</summary>
    MadeOrganizer,
    /// <summary>After a draw (or a redraw's new draw): every participant.</summary>
    NamesDrawn,
    /// <summary><c>drawn → revealed</c>: every participant and organizer.</summary>
    EventRevealed,
    /// <summary>A giver posted in the recipient's thread; held to the next full hour.</summary>
    Question,
    /// <summary>A participant's request to leave needs a redraw: every organizer.</summary>
    RedrawNeeded,
}
