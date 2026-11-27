namespace Pollyanna.Live;

/// <summary>
/// A live refetch (spec §16 "A live refetch isn't a visit"): the page's script, on a hint, fetches
/// the page's own URL again with this request header. No new endpoint. The server renders the page
/// as usual but treats it as a read-only view: no read marker or <c>NotesVersionSeen</c> moves, no
/// <c>RecipientChanged</c> or <c>ThreadChangeUnseen</c> clears, no stored notification is deleted
/// and no <c>Viewed</c> is set. Only a real navigation or a deliberate post counts as seeing.
/// </summary>
public static class LiveRefetch
{
    /// <summary>The request header that marks a fetch as a live refetch.</summary>
    public const string Header = "Pollyanna-Live";

    /// <summary>The value the script sends; any value counts.</summary>
    public const string Value = "1";

    /// <summary>
    /// Whether this request is a live refetch. A page's GET checks it before anything that counts
    /// as seeing (moving a marker, deleting a stored notification).
    /// </summary>
    public static bool IsLiveRefetch(this HttpRequest request) => request.Headers.ContainsKey(Header);
}
