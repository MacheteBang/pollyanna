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
/// Maps a provider account to a Pollyanna person on every sign-in (spec §3): find the person
/// holding the login (issuer + sub), else add the login to the one person whose current email
/// matches a verified email, else create a person. Then refresh name, email and picture from
/// the claims just received (a claim that isn't sent clears nothing), and turn any invitations
/// to that email into participation (spec §4).
/// </summary>
public sealed class PersonSignIn(PollyannaDb db, PictureFetcher pictures, TimeProvider clock, EmailVerifiedHint hint, Outbox outbox, ILogger<PersonSignIn> logger)
{
    public async Task<Person> SignInAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (identity.EmailVerified is null)
            hint.Note(logger);

        // Deleting an account deletes its logins, so a login always belongs to a living person.
        var login = await db.Logins.SingleOrDefaultAsync(
            l => l.Issuer == identity.Issuer && l.Subject == identity.Subject, cancellationToken);
        Person person;
        if (login is not null)
            person = await db.People.SingleAsync(p => p.Id == login.PersonId, cancellationToken);
        else
        {
            login = new Login { Issuer = identity.Issuer, Subject = identity.Subject, CreatedAt = now };
            if (await MatchByEmailAsync(identity, cancellationToken) is { } match)
            {
                person = match;
                logger.LogInformation("Added a login to person {PersonId} by verified email.", person.Id);
            }
            else
            {
                person = new Person { CreatedAt = now };
                db.People.Add(person);
            }
            login.PersonId = person.Id;
            db.Logins.Add(login);
        }

        login.LastUsedAt = now;
        if (identity.Email is not null)
            login.LastEmail = identity.Email;
        UpdateProfile(person, identity);
        person.LastLoginAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await AcceptInvitationsAsync(person, now, cancellationToken);

        await RefreshPictureAsync(person, identity, cancellationToken);
        return person;
    }

    /// <summary>
    /// The one living person whose current email matches, case-insensitively, an email the
    /// provider explicitly marked verified. Two or more matches link nobody, and say so in one
    /// line that names nobody.
    /// </summary>
    async Task<Person?> MatchByEmailAsync(ProviderIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.EmailVerified != true || Person.NormalizeEmail(identity.Email) is not { } email)
            return null;
        var matches = await db.People.Living().Where(p => p.EmailNormalized == email)
            .OrderBy(p => p.Id).ToListAsync(cancellationToken);
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
            logger.LogInformation("A new login's verified email matches {Count} people, so it was linked to none of them.", matches.Count);
        return null;
    }

    /// <summary>Name, email and picture take the values just received; a claim that isn't sent keeps what's stored.</summary>
    static void UpdateProfile(Person person, ProviderIdentity identity)
    {
        if (identity.Name is not null)
            person.DisplayName = identity.Name;
        if (identity.Email is not null)
        {
            person.Email = identity.Email;
            person.EmailNormalized = Person.NormalizeEmail(identity.Email);
            person.EmailVerified = identity.EmailVerified;
        }
        person.DisplayName ??= identity.Email ?? "Unnamed";
        if (identity.PictureUrl is not null)
            person.PictureUrl = identity.PictureUrl;
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

    async Task RefreshPictureAsync(Person person, ProviderIdentity identity, CancellationToken cancellationToken)
    {
        // No picture sent this time keeps the stored one (spec §3).
        if (identity.PictureUrl is null)
            return;
        var image = await pictures.FetchAsync(identity.PictureUrl, cancellationToken);
        if (image is null)
            return; // Keep whatever we had; a failed fetch never blocks sign-in.

        var stored = await db.PersonPictures.FindAsync([person.Id], cancellationToken);

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
