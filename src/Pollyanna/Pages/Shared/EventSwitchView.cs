namespace Pollyanna.Pages.Shared;

/// <summary>
/// The two-way switch at the top of the event page and the organizing page (spec §7 Page head),
/// for an organizer who also takes part: "My page" (<c>/events/{id}</c>) and "Organizing"
/// (<c>/events/{id}/manage</c>). <paramref name="Organizing"/> says which side the viewer is on.
/// It replaces the back link in the page head.
/// </summary>
public sealed record EventSwitchView(Guid EventId, bool Organizing);
