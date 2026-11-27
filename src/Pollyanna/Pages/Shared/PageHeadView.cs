namespace Pollyanna.Pages.Shared;

/// <summary>
/// The "Tablecloth" page head under the masthead (spec §7 Page head): a small back link, the page
/// title, then one small soft line. <paramref name="BackText"/> and <paramref name="BackHref"/> make
/// the back link ("Home" to <c>/</c>, or the event's name on its sub-pages); leave them null for
/// none. <paramref name="Line"/> is the small line, or null. A page with the two-way switch
/// ("My page" / "Organizing") renders the switch itself in place of the back link.
/// </summary>
public sealed record PageHeadView(string Title, string? Line = null, string? BackText = null, string? BackHref = null);
