using Pollyanna.Events;

namespace Pollyanna.Pages.Events;

/// <summary>
/// The add-people control's fields on the organizing page (spec §7): the people the viewer knows,
/// names and pictures only, and what was ticked and typed when a refused form comes back.
/// </summary>
public sealed record AddPeopleControl(IReadOnlyList<PersonRow> Known, IReadOnlyCollection<Guid> Ticked, string? Email);
