using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications;

/// <summary>Who a notification kind is addressed to, which is also the role it is dropped without (spec §15).</summary>
public enum Audience
{
    /// <summary>Participants of the event.</summary>
    Participants,
    /// <summary>Organizers of the event.</summary>
    Organizers,
    /// <summary>Anyone who is a participant or an organizer of the event.</summary>
    ParticipantsAndOrganizers,
    /// <summary>An invited email address with a pending invitation to the event.</summary>
    Invitee,
}

/// <summary>What each <see cref="Audience"/> means: who is in it, and what the footer says to them.</summary>
public static class AudienceRules
{
    /// <summary>True when rows hold an email address rather than a person.</summary>
    public static bool ByEmail(this Audience audience) => audience == Audience.Invitee;

    /// <summary>The people in <paramref name="audience"/> for the event, as the database stands.</summary>
    public static IQueryable<Guid> Members(this Audience audience, PollyannaDb db, Guid eventId)
    {
        var participants = db.Participants.Where(p => p.EventId == eventId).Select(p => p.PersonId);
        var organizers = db.Organizers.Where(o => o.EventId == eventId).Select(o => o.PersonId);
        return audience switch
        {
            Audience.Participants => participants,
            Audience.Organizers => organizers,
            Audience.ParticipantsAndOrganizers => participants.Union(organizers),
            _ => throw new ArgumentException($"{audience} is an invited address, not people in the event."),
        };
    }

    /// <summary>
    /// Whether <paramref name="row"/>'s recipient is still in <paramref name="audience"/>: a person
    /// still has the role, an invited address still has a pending invitation.
    /// </summary>
    public static async Task<bool> StillHasAsync(this Audience audience, PollyannaDb db, Notification row, CancellationToken cancellationToken)
    {
        if (audience.ByEmail())
            return row.Email is { } email && await db.Invitations.AnyAsync(
                i => i.EventId == row.EventId && i.EmailNormalized == Person.NormalizeEmail(email), cancellationToken);
        return await audience.Members(db, row.EventId).AnyAsync(p => p == row.PersonId, cancellationToken);
    }

    /// <summary>
    /// Why the email came and how to stop them (spec §15), one line each, as the plain text has
    /// it. An invitation's footer says to ignore it instead.
    /// </summary>
    public static string Footer(this Audience audience, Event ev, string publicUrl) =>
        string.Join("\n", audience.FooterLines(ev, publicUrl));

    /// <summary>The lines of <see cref="Footer"/>, each with the link it ends with, if any.</summary>
    public static IReadOnlyList<FooterLine> FooterLines(this Audience audience, Event ev, string publicUrl)
    {
        var stop = new FooterLine("To stop Pollyanna emails, switch them off on your account page:", $"{publicUrl}/account");
        return audience switch
        {
            Audience.Invitee => [new($"Someone invited this address to \"{ev.Name}\" on Pollyanna. If this email wasn't meant for you, ignore it.")],
            Audience.Participants => [new($"You're getting this because you take part in \"{ev.Name}\" on Pollyanna."), stop],
            Audience.Organizers => [new($"You're getting this because you organize \"{ev.Name}\" on Pollyanna."), stop],
            _ => [new($"You're getting this because you're in \"{ev.Name}\" on Pollyanna."), stop],
        };
    }
}

/// <summary>One line of an email's footer: its text, then the link it ends with, if any.</summary>
public sealed record FooterLine(string Text, string? Link = null)
{
    /// <summary>The line as the plain text has it.</summary>
    public override string ToString() => Link is null ? Text : $"{Text} {Link}";
}
