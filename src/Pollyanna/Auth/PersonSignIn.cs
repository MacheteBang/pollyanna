using System.Security.Claims;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Notifications;
using Pollyanna.Notifications.Kinds;
using Pollyanna.People;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Auth;

/// <summary>What the provider told us about the person signing in.</summary>
public sealed record ProviderIdentity(
    string Issuer,
    string Subject,
    string? Name,
    string? Email,
    bool? EmailVerified,
    string? PictureUrl)
{
    public static ProviderIdentity FromClaims(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst("sub") ?? throw new InvalidOperationException("The provider sent no sub claim.");
        string? Value(string type) => principal.FindFirst(type)?.Value is { Length: > 0 } v ? v.Trim() : null;

        bool? verified = Value("email_verified") switch
        {
            null => null,
            var v when bool.TryParse(v, out var b) => b,
            _ => false,
        };

        return new ProviderIdentity(
            sub.Issuer,
            sub.Value,
            Value("name") ?? Value("preferred_username"),
            Value("email"),
            verified,
            Value("picture"));
    }
}

/// <summary>
/// Maps a provider account to a Pollyanna person on every sign-in (spec §3): find by issuer +
/// sub, else re-link by verified email, else create. Then refresh name, email and picture, and
/// turn any invitations to that email into participation (spec §4).
/// </summary>
public sealed class PersonSignIn(PollyannaDb db, PictureFetcher pictures, TimeProvider clock, EmailVerifiedHint hint, Outbox outbox, ILogger<PersonSignIn> logger)
{
    public async Task<Person> SignInAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (identity.EmailVerified is null)
            hint.Note(logger);
        var person = await db.People.SingleOrDefaultAsync(
            p => p.OidcIssuer == identity.Issuer && p.OidcSub == identity.Subject, cancellationToken);

        if (person is null && identity.EmailVerified == true && Person.NormalizeEmail(identity.Email) is { } email)
        {
            // Re-linking needs an explicit email_verified=true, and exactly one match.
            var matches = await db.People.Where(p => p.EmailNormalized == email).OrderBy(p => p.Id).Take(2).ToListAsync(cancellationToken);
            if (matches.Count == 1)
            {
                person = matches[0];
                logger.LogInformation("Re-linked person {PersonId} to a new provider account.", person.Id);
                person.OidcIssuer = identity.Issuer;
                person.OidcSub = identity.Subject;
            }
        }

        if (person is null)
        {
            person = new Person { OidcIssuer = identity.Issuer, OidcSub = identity.Subject, CreatedAt = now };
            db.People.Add(person);
        }

        person.DisplayName = identity.Name ?? identity.Email ?? "Unnamed";
        person.Email = identity.Email;
        person.EmailNormalized = Person.NormalizeEmail(identity.Email);
        person.EmailVerified = identity.Email is null ? null : identity.EmailVerified;
        person.PictureUrl = identity.PictureUrl;
        person.LastLoginAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await AcceptInvitationsAsync(person, now, cancellationToken);

        await RefreshPictureAsync(person, cancellationToken);
        return person;
    }

    /// <summary>
    /// Each invitation to the person's email becomes a participant row and is deleted. Matching
    /// is case-insensitive; an email the provider marks unverified is ignored, but one with no
    /// <c>email_verified</c> claim counts (unlike re-linking). The person is emailed "Added to an
    /// event" for each event that isn't a draft (spec §15).
    /// </summary>
    async Task AcceptInvitationsAsync(Person person, DateTime now, CancellationToken cancellationToken)
    {
        if (person.EmailNormalized is not { } email || person.EmailVerified == false)
            return;

        var invitations = await db.Invitations.Where(i => i.EmailNormalized == email).ToListAsync(cancellationToken);
        if (invitations.Count == 0)
            return;

        var eventIds = invitations.Select(i => i.EventId).ToList();
        var already = await db.Participants
            .Where(p => p.PersonId == person.Id && eventIds.Contains(p.EventId))
            .Select(p => p.EventId).ToListAsync(cancellationToken);
        var joined = eventIds.Distinct().Except(already).ToList();
        var events = await db.Events.Where(e => joined.Contains(e.Id)).ToListAsync(cancellationToken);
        foreach (var ev in events)
        {
            db.Participants.Add(new Participant { EventId = ev.Id, PersonId = person.Id, JoinedAt = now });
            await outbox.QueueAsync(new AddedToEvent.Cause(ev.Id, person.Id, Actor: null));
        }
        db.Invitations.RemoveRange(invitations);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Person {PersonId} joined {Count} event(s) by invitation.", person.Id, invitations.Count);
    }

    async Task RefreshPictureAsync(Person person, CancellationToken cancellationToken)
    {
        var stored = await db.PersonPictures.FindAsync([person.Id], cancellationToken);
        if (person.PictureUrl is null)
        {
            // The provider no longer sends a picture: fall back to initials.
            if (stored is not null)
            {
                db.PersonPictures.Remove(stored);
                await db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var image = await pictures.FetchAsync(person.PictureUrl, cancellationToken);
        if (image is null)
            return; // Keep whatever we had; a failed fetch never blocks sign-in.

        if (stored is null)
            db.PersonPictures.Add(new PersonPicture { PersonId = person.Id, Data = image.Data, ContentType = image.ContentType });
        else
        {
            stored.Data = image.Data;
            stored.ContentType = image.ContentType;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Whether the provider sends <c>email_verified</c> is its configuration, not the person's: tell
/// the operator once per process run, naming nobody (spec §3).
/// </summary>
public sealed class EmailVerifiedHint
{
    int noted;

    public void Note(ILogger logger)
    {
        if (Interlocked.Exchange(ref noted, 1) == 0)
            logger.LogInformation(
                "The OIDC provider sent no email_verified claim. Invitations by email still match, but re-linking a "
                + "recreated account by email won't work for people signing in this way. Turn on email_verified at the "
                + "provider if it has the setting.");
    }
}
