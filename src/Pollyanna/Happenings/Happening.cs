namespace Pollyanna.Happenings;

/// <summary>
/// Something that happened in an event (GLOSSARY.md "Happening", spec §16), as its source
/// announces it to <see cref="Announcer"/>. Holds IDs only; whatever acts on it reads the rest.
/// </summary>
/// <param name="Actor">Who did it; null when nobody did, as at sign-in. Nobody is told about their own action.</param>
/// <param name="About">Who or what it is about, as <see cref="HappeningKind"/> says for each kind; null if nothing.</param>
public sealed record Happening(HappeningKind Kind, Guid EventId, Guid? Actor, Guid? About = null);

/// <summary>Hears every happening as it is announced, inside the transaction of the change that made it.</summary>
public interface IHappeningListener
{
    Task HearAsync(Happening happening);
}
