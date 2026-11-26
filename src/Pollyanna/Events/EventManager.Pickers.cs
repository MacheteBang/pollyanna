using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>
/// The organizer and late-joiner pickers (spec §4 Organizer, §5.4): one person picked from the
/// viewer's list (<see cref="KnownTo"/>), or the one person a typed address belongs to. Unlike the
/// add-people control, an address here never invites: an organizer must already be a person
/// (have signed in), and invitations can't be used while names are drawn.
/// </summary>
public sealed partial class EventManager
{
    public const string NobodyForOrganizer =
        "Nobody uses that address in Pollyanna yet. An organizer needs to have signed in first.";

    public const string NobodyForLateJoiner =
        "Nobody uses that address in Pollyanna yet. Invitations wait until the names are back in the hat, so they need to sign in first.";

    /// <summary>
    /// The person behind a picker post: <paramref name="personId"/>, or whoever uses
    /// <paramref name="email"/>. Nobody found, or several, is a refusal naming nobody, and nothing
    /// is stored. The caller checks that <paramref name="personId"/> is one the viewer may pick.
    /// </summary>
    public async Task<(Person? Person, Outcome? Refusal)> PickOneAsync(Guid? personId, string? email, string nobody)
    {
        email = email?.Trim() ?? "";
        if (personId is not null && email != "")
            return (null, Outcome.Fail("Pick one person, or type their email, not both."));
        if (personId is { } id)
        {
            var person = await db.People.Living().SingleOrDefaultAsync(p => p.Id == id);
            return person is null ? (null, Outcome.Fail("Pick someone from the list.")) : (person, null);
        }
        if (email == "")
            return (null, Outcome.Fail("Pick someone, or type their email."));
        if (!IsEmail(email))
            return (null, Outcome.Fail("That doesn't look like an email address."));

        var matches = await UsingAddress(Person.NormalizeEmail(email)!).OrderBy(p => p.Id).Take(2).ToListAsync();
        return matches.Count switch
        {
            0 => (null, Outcome.Fail(nobody)),
            1 => (matches[0], null),
            _ => (null, Outcome.Fail(AmbiguousAddress)),
        };
    }

    /// <summary>
    /// The people an address belongs to, matched as on sign-in: case-insensitive, and never an
    /// email marked unverified.
    /// </summary>
    IQueryable<Person> UsingAddress(string normalized) =>
        db.People.Living().Where(p => p.EmailNormalized == normalized && p.EmailVerified != false);
}
