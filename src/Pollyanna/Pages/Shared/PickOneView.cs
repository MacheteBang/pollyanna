using Pollyanna.Events;

namespace Pollyanna.Pages.Shared;

/// <summary>
/// The inside of a one-person picker (spec §4 Organizer, §5.4): the viewer's people as a radio
/// list, names and pictures only, and an email field that only finds an existing person. It is
/// drawn like the add-people control and uses its script. <paramref name="Prefix"/> keeps the
/// element IDs apart; <paramref name="Finds"/> says what a found address leads to, such as
/// "they're made an organizer".
/// </summary>
public sealed record PickOneView(string Prefix, IReadOnlyList<PersonRow> People, string Legend, string Finds);
