using System.Security.Claims;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Notifications;
using Pollyanna.Happenings;
using Pollyanna.People;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Auth;

/// <summary>How adding a login from <c>/account</c> ended.</summary>
public enum AddLoginResult { Added, AlreadyYours, BelongsToSomeoneElse }

/// <summary>How removing a login from <c>/account</c> ended.</summary>
public enum RemoveLoginResult { Removed, NotYours, LastLogin }

/// <summary>What the provider told us about the person signing in.</summary>
public sealed record ProviderIdentity(
    string Issuer,
    string Subject,
    string? Name,
    string? Email,
    bool? EmailVerified,
    string? PictureUrl)
{
    /// <summary>The normalized email, only when the provider explicitly marked it verified; null otherwise.</summary>
    public string? VerifiedEmail => EmailVerified == true ? Person.NormalizeEmail(Email) : null;

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

/// <summary>Why an open provider's sign-in was refused (spec §3).</summary>
public enum RefusalReason
{
    /// <summary>The provider didn't send <c>email_verified: true</c> (a missing claim counts as false).</summary>
    EmailNotVerified,
    /// <summary>No person and no pending invitation has the address.</summary>
    NoInvitationOrPerson,
    /// <summary>Two or more people have the address.</summary>
    Ambiguous,
}

/// <summary>A refused sign-in: the reason, the address just used, and for an ambiguous one, how many people match.</summary>
public sealed record SignInRefusal(RefusalReason Reason, string? Address, int Matches = 0)
{
    /// <summary>The reason as the log gives it, naming nobody.</summary>
    public string LogText => Reason switch
    {
        RefusalReason.EmailNotVerified => "email not verified",
        RefusalReason.NoInvitationOrPerson => "no invitation or person",
        _ => $"ambiguous: {Matches} matches",
    };
}

/// <summary>Either the person now signed in, or why the sign-in was refused.</summary>
public sealed record SignInOutcome(Person? Person, SignInRefusal? Refusal);

/// <summary>
/// Maps a provider account to a Pollyanna person on every sign-in (spec §3): find the person
/// holding the login (issuer + sub), else add the login to the one person whose current email
/// matches a verified email, else create a person. An open provider creates a person only for a
/// verified email with a pending invitation, and refuses anyone else with nothing stored. Then
/// refresh name, email and picture from the claims just received (a claim that isn't sent clears
/// nothing), and turn any invitations to that email into participation (spec §4).
/// </summary>
public sealed class PersonSignIn(PollyannaDb db, PictureFetcher pictures, TimeProvider clock, EmailVerifiedHint hint, Announcer announcer,
    ProviderRegistry providers, ILogger<PersonSignIn> logger)
{
    public async Task<SignInOutcome> SignInAsync(Provider provider, ProviderIdentity identity, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (identity.EmailVerified is null)
            hint.Note(logger, provider);

        // Deleting an account deletes its logins, so a login always belongs to a living person.
        // A known login always signs in, whatever the provider's admission.
        var login = await db.Logins.SingleOrDefaultAsync(
            l => l.Issuer == identity.Issuer && l.Subject == identity.Subject, cancellationToken);
        Person person;
        if (login is not null)
            person = await db.People.SingleAsync(p => p.Id == login.PersonId, cancellationToken);
        else
        {
            var matches = await VerifiedEmailMatchesAsync(identity, cancellationToken);
            if (matches.Count == 1)
            {
                person = matches[0];
                logger.LogInformation("Added a login to person {PersonId} by verified email.", person.Id);
            }
            else if (provider.Admission == Admission.Open && await AdmitAsync(identity, matches.Count, cancellationToken) is { } refusal)
            {
                logger.LogInformation("Refused a sign-in through provider {Provider}: {Reason}.", provider.Key, refusal.LogText);
                return new SignInOutcome(null, refusal);
            }
            else
            {
                if (matches.Count > 1)
                    logger.LogInformation("A new login's verified email matches {Count} people, so it was linked to none of them.", matches.Count);
                person = new Person { CreatedAt = now };
                db.People.Add(person);
            }
            login = new Login { Issuer = identity.Issuer, Subject = identity.Subject, CreatedAt = now, PersonId = person.Id };
            db.Logins.Add(login);
        }

        await CountAsSignInAsync(provider, person, login, identity, now, cancellationToken);
        return new SignInOutcome(person, null);
    }

    /// <summary>
    /// Adds the login just returned by <paramref name="provider"/> to <paramref name="personId"/>,
    /// who asked for it from <c>/account</c> (spec §3 "Adding a login while signed in"). The fresh
    /// round trip proves control, so there is no email match, no <c>email_verified</c> check and no
    /// admission check. A login another person holds is refused and nothing is merged; one the
    /// person already holds changes nothing. An added login counts as a sign-in for the profile
    /// and for invitations, under the same open-provider rule as any sign-in (spec §4).
    /// </summary>
    public async Task<AddLoginResult> AddLoginAsync(Provider provider, Guid personId, ProviderIdentity identity, CancellationToken cancellationToken = default)
    {
        var holder = await db.Logins.Where(l => l.Issuer == identity.Issuer && l.Subject == identity.Subject)
            .Select(l => (Guid?)l.PersonId).SingleOrDefaultAsync(cancellationToken);
        if (holder == personId)
            return AddLoginResult.AlreadyYours;
        if (holder is not null)
        {
            logger.LogInformation("Person {PersonId} tried to add a login through provider {Provider} that belongs to another person.",
                personId, provider.Key);
            return AddLoginResult.BelongsToSomeoneElse;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var person = await db.People.Living().SingleAsync(p => p.Id == personId, cancellationToken);
        var login = new Login { PersonId = personId, Issuer = identity.Issuer, Subject = identity.Subject, CreatedAt = now };
        db.Logins.Add(login);
        await CountAsSignInAsync(provider, person, login, identity, now, cancellationToken);
        logger.LogInformation("Person {PersonId} added a login through provider {Provider}.", personId, provider.Key);
        return AddLoginResult.Added;
    }

    /// <summary>
    /// Removes one of <paramref name="personId"/>'s logins, any but the last (spec §3 "Removing").
    /// A login that isn't theirs is <see cref="RemoveLoginResult.NotYours"/>. The session carries
    /// on, even when it was made through the login removed.
    /// </summary>
    public async Task<RemoveLoginResult> RemoveLoginAsync(Guid personId, Guid loginId, CancellationToken cancellationToken = default)
    {
        var issuer = await db.Logins.Where(l => l.Id == loginId && l.PersonId == personId)
            .Select(l => l.Issuer).SingleOrDefaultAsync(cancellationToken);
        if (issuer is null)
            return RemoveLoginResult.NotYours;
        // One statement, so two removals at once can't leave the person with none.
        var removed = await db.Logins
            .Where(l => l.Id == loginId && db.Logins.Any(o => o.PersonId == personId && o.Id != loginId))
            .ExecuteDeleteAsync(cancellationToken);
        if (removed == 0)
            return RemoveLoginResult.LastLogin;

        if (providers.ByIssuer(issuer) is { } provider)
            logger.LogInformation("Person {PersonId} removed a login through provider {Provider}.", personId, provider.Key);
        else
            logger.LogInformation("Person {PersonId} removed a login through a provider that is no longer set up.", personId);
        return RemoveLoginResult.Removed;
    }

    /// <summary>
    /// What every sign-in does once the login and person are known: refresh both, accept
    /// invitations, fetch the picture. Through an open provider only <c>email_verified: true</c>
    /// accepts invitations; a missing claim counts as false (spec §4).
    /// </summary>
    async Task CountAsSignInAsync(Provider provider, Person person, Login login, ProviderIdentity identity, DateTime now, CancellationToken cancellationToken)
    {
        login.LastUsedAt = now;
        if (identity.Email is not null)
            login.LastEmail = identity.Email;
        UpdateProfile(person, identity);
        person.LastLoginAt = now;
        await db.SaveChangesAsync(cancellationToken);

        if (provider.Admission == Admission.Trusted || identity.EmailVerified == true)
            await AcceptInvitationsAsync(person, now, cancellationToken);

        await RefreshPictureAsync(person, identity, cancellationToken);
    }

    /// <summary>
    /// The living people whose current email matches, case-insensitively, an email the provider
    /// explicitly marked verified; none for any other email.
    /// </summary>
    async Task<List<Person>> VerifiedEmailMatchesAsync(ProviderIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.VerifiedEmail is not { } email)
            return [];
        return await db.People.Living().Where(p => p.EmailNormalized == email)
            .OrderBy(p => p.Id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The open provider's gate for a login nobody holds and no single person matches: a verified
    /// email that no two people share, with at least one pending invitation. Null admits.
    /// Pending invitations live only in draft and open events; a canceled one is deleted.
    /// </summary>
    async Task<SignInRefusal?> AdmitAsync(ProviderIdentity identity, int matches, CancellationToken cancellationToken)
    {
        if (identity.VerifiedEmail is not { } email)
            return new SignInRefusal(RefusalReason.EmailNotVerified, identity.Email);
        if (matches > 1)
            return new SignInRefusal(RefusalReason.Ambiguous, identity.Email, matches);
        var invited = await db.Invitations.AnyAsync(i => i.EmailNormalized == email
            && db.Events.Any(e => e.Id == i.EventId && (e.Status == EventStatus.Draft || e.Status == EventStatus.Open)), cancellationToken);
        return invited ? null : new SignInRefusal(RefusalReason.NoInvitationOrPerson, identity.Email);
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
    /// <c>email_verified</c> claim counts (unlike linking by email). The caller skips this
    /// entirely for an open provider that didn't send <c>email_verified: true</c> (spec §4). The
    /// person is emailed "Added to an event" for each event that isn't a draft (spec §15).
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
            await announcer.AnnounceAsync(new Happening(HappeningKind.InvitationAccepted, ev.Id, Actor: null, About: person.Id));
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
/// the operator once per provider per process run, naming the provider and nobody else, with
/// what a missing claim means for its admission (spec §3).
/// </summary>
public sealed class EmailVerifiedHint
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> noted = new();

    public void Note(ILogger logger, Provider provider)
    {
        if (!noted.TryAdd(provider.Key, true))
            return;
        if (provider.Admission == Admission.Open)
            logger.LogInformation(
                "The OIDC provider {Provider} sent no email_verified claim. It is an open provider, so new sign-ins through it "
                + "are refused and it accepts no invitations: a missing claim counts as unverified. Turn on email_verified at "
                + "the provider if it has the setting.", provider.Key);
        else
            logger.LogInformation(
                "The OIDC provider {Provider} sent no email_verified claim. Invitations by email still match, but adding a "
                + "login to someone by verified email won't happen for people signing in this way. Turn on email_verified at "
                + "the provider if it has the setting.", provider.Key);
    }
}
