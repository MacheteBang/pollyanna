namespace Pollyanna.Events;

/// <summary>A person as the people lists show them: name and, if stored, their picture.</summary>
public record PersonRow(Guid Id, string Name, bool HasPicture);
