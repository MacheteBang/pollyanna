namespace Pollyanna.Pages.Shared;

/// <summary>
/// The two-way switch at the top of an event's two pages (spec §7 Page head), for an organizer who
/// also takes part: "My page" (<c>/events/{id}</c>) and "Organizing" (<c>/events/{id}/manage</c>).
/// <paramref name="Organizing"/> says which of the two is the current page. It takes the back link's
/// place; an organizer who doesn't take part sees no switch.
/// </summary>
public sealed record EventSwitchView(Guid EventId, bool Organizing);
