namespace Pollyanna.Events;

/// <summary>
/// The question box's posting limit (spec §6, §8): <see cref="PerHour"/> messages per person in
/// any hour, across all their sessions, as a sliding window. Counted in memory only, so nothing
/// stored says when anyone posted; a restart forgets the counts.
/// </summary>
public sealed class PostingLimit(TimeProvider clock)
{
    public const int DefaultPerHour = 20;
    static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Fixed in the app; tests change it.</summary>
    public int PerHour { get; set; } = DefaultPerHour;

    readonly Dictionary<Guid, Queue<DateTimeOffset>> posts = [];
    readonly Lock gate = new();

    /// <summary>Counts a post for <paramref name="person"/>, or returns false if they're at the limit.</summary>
    public bool TryTake(Guid person)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            // Forget whoever has been quiet for an hour, so the table stays small.
            foreach (var (id, times) in posts)
            {
                while (times.Count > 0 && now - times.Peek() >= Window)
                    times.Dequeue();
                if (times.Count == 0)
                    posts.Remove(id);
            }

            if (!posts.TryGetValue(person, out var mine))
                posts[person] = mine = new Queue<DateTimeOffset>();
            if (mine.Count >= PerHour)
                return false;
            mine.Enqueue(now);
            return true;
        }
    }
}
