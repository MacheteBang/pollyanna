using Pollyanna.Events;

namespace Pollyanna.Pages.Events;

/// <summary>
/// The add-people control's fields (spec §7), shared by the manage page and the new-event form:
/// the people the viewer knows, names and pictures only, and what was ticked and typed when a
/// refused form comes back. On the new-event form, <paramref name="PreviousGroups"/> names the
/// earlier events each person is prefilled from, and anyone prefilled from
/// <paramref name="Linked"/> is hidden from the list, since they are already ticked there (spec §4).
/// </summary>
public sealed record AddPeopleControl(IReadOnlyList<PersonRow> Known, IReadOnlyCollection<Guid> Ticked, string? Email,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? PreviousGroups = null, Guid? Linked = null)
{
    public IReadOnlyList<Guid> GroupsOf(Guid personId) =>
        PreviousGroups is not null && PreviousGroups.TryGetValue(personId, out var groups) ? groups : [];

    public bool Prefilled(Guid personId) => Linked is { } linked && GroupsOf(personId).Contains(linked);
}
