namespace Pollyanna.Pages.Shared;

/// <summary>
/// The "Tablecloth" page head under the masthead (spec §7 Page head): a small back link, the page
/// title, then one small soft line. <paramref name="Back"/> is the back link ("Home" to <c>/</c>,
/// or the event's name on its sub-pages), or null for none. <paramref name="Line"/> is the small
/// line, or null. The event and organizing pages render their own head, with the two-way switch
/// ("My page" / "Organizing") under the title.
/// </summary>
public sealed record PageHeadView(string Title, string? Line = null, BackLink? Back = null);

/// <summary>A page head's back link: its words and where it goes.</summary>
public sealed record BackLink(string Text, string Href);
