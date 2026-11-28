namespace Pollyanna.Live;

/// <summary>
/// A live refetch (spec §16 "A live refetch isn't a visit"): the page's script fetches the page's
/// own URL again with this request header. No new endpoint. The server renders the page as usual
/// but treats it as a read-only view: no read marker or <c>NotesVersionSeen</c> moves, no
/// <c>RecipientChanged</c> or <c>ThreadChangeUnseen</c> clears, no stored notification is deleted
/// and no <c>Viewed</c> is set. Only a real navigation or a deliberate post counts as seeing.
/// <para>
/// The header's value says why. On a hint (<see cref="Hint"/>) the page leaves out the parts
/// between a giver and a recipient (<see cref="CarriesPairParts"/>), since a hint sets off a refetch
/// from everyone it reaches, and a response that changed only for one giver would pair them in
/// the proxy log by its size. Catching up (<see cref="CatchUp"/>: shown again, reconnected, or a
/// person's own action in a pair, which only their own tabs hear of) carries everything.
/// </para>
/// </summary>
public static class LiveRefetch
{
    /// <summary>The request header that marks a fetch as a live refetch.</summary>
    public const string Header = "Pollyanna-Live";

    /// <summary>The header's value on a refetch a hint set off: the pair parts are left out.</summary>
    public const string Hint = "hint";

    /// <summary>The header's value on any other live refetch (any value but <see cref="Hint"/> counts): everything is in it.</summary>
    public const string CatchUp = "catch-up";

    /// <summary>
    /// Whether this request is a live refetch. A page's GET checks it before anything that counts
    /// as seeing (moving a marker, deleting a stored notification).
    /// </summary>
    public static bool IsLiveRefetch(this HttpRequest request) => request.Headers.ContainsKey(Header);

    /// <summary>
    /// False on a refetch a hint set off, which leaves the pair parts out: the giver's
    /// recipient (their name, notes, "Notes updated since you last looked", the question box,
    /// the patch line), the recipient's questions and the lines pointing to them, home's waiting
    /// line in a drawn event, and "What's new"'s five worked-out rows and the bell's count of
    /// them. Each part is still there, empty and marked <c>data-live-withheld</c>, so the script
    /// keeps the one it has (spec §16).
    /// </summary>
    public static bool CarriesPairParts(this HttpRequest request) =>
        !(request.Headers.TryGetValue(Header, out var value) && value == Hint);
}
