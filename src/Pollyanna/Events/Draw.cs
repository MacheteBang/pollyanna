using System.Security.Cryptography;

namespace Pollyanna.Events;

/// <summary>
/// Who may draw whom (spec §5.1): nobody themselves, nobody in their own household, nobody
/// listed with them in an exclusion, and no pair in <see cref="Forbidden"/> (giver → recipient,
/// one way), which is where the no-repeat rule plugs in.
/// </summary>
public sealed record DrawRules(
    IReadOnlyList<Guid> People,
    IReadOnlyDictionary<Guid, Guid> HouseholdOf,
    IReadOnlyCollection<(Guid A, Guid B)> Exclusions,
    IReadOnlyCollection<(Guid Giver, Guid Recipient)> Forbidden)
{
    /// <summary>Names for the impossibility messages: people, and households by their ID.</summary>
    public IReadOnlyDictionary<Guid, string> Names { get; init; } = new Dictionary<Guid, string>();

    readonly HashSet<(Guid, Guid)> excluded = [.. Exclusions.SelectMany(x => new[] { (x.A, x.B), (x.B, x.A) })];
    readonly HashSet<(Guid, Guid)> forbidden = [.. Forbidden];

    public bool Allows(Guid giver, Guid recipient) =>
        giver != recipient
        && !(HouseholdOf.TryGetValue(giver, out var a) && HouseholdOf.TryGetValue(recipient, out var b) && a == b)
        && !excluded.Contains((giver, recipient))
        && !forbidden.Contains((giver, recipient));

    public string Name(Guid id) => Names.GetValueOrDefault(id, "Someone");
}

/// <summary>The outcome of a draw: every giver's recipient, or why there is none.</summary>
public sealed record DrawResult(IReadOnlyDictionary<Guid, Guid>? Pairs, string? Problem)
{
    public bool Ok => Pairs is not null;
}

/// <summary>
/// The draw itself (spec §5.1). Pairings exist only in the returned dictionary: nothing here
/// stores or logs them. All randomness comes from the system CSPRNG.
/// </summary>
public static class Draw
{
    public const int MinPeople = 3;
    /// <summary>Below this, every valid assignment is equally likely; from it, one loop is preferred.</summary>
    public const int SingleCycleFrom = 6;
    public const int SingleCycleAttempts = 10_000;
    /// <summary>The predictability warning's thresholds (spec §5.1), defaults to tune.</summary>
    public const int PredictableBelowPeople = 5;
    public const int PredictableBelowAssignments = 20;

    public static DrawResult Run(DrawRules rules)
    {
        var graph = new Graph(rules);
        if (graph.N < MinPeople)
            return new(null, $"The hat needs at least {MinPeople} people.");
        if (!graph.CanComplete(new int[graph.N].Fill(-1)))
            return new(null, WhyImpossible(rules, graph));

        int[]? result = null;
        if (graph.N < SingleCycleFrom)
        {
            // Tiny draws: a forced single loop would make them more predictable, so pick
            // uniformly among all valid assignments.
            var all = graph.Assignments(int.MaxValue).ToList();
            result = all[RandomNumberGenerator.GetInt32(all.Count)];
        }
        else
        {
            for (var i = 0; i < SingleCycleAttempts && result is null; i++)
                result = graph.TrySingleCycle();
            result ??= graph.RandomAssignment();
        }
        return new(Enumerable.Range(0, graph.N).ToDictionary(g => graph.People[g], g => graph.People[result[g]]), null);
    }

    /// <summary>Counts valid assignments, stopping once <paramref name="limit"/> is reached.</summary>
    public static int Count(DrawRules rules, int limit) => new Graph(rules).Assignments(limit).Count();

    /// <summary>
    /// Whether the manage page should warn that pairings may be guessable (spec §5.1): few people,
    /// or few valid assignments under the rules. An impossible draw gets its own message instead.
    /// </summary>
    public static bool IsPredictable(DrawRules rules) =>
        rules.People.Count < PredictableBelowPeople
        || Count(rules, PredictableBelowAssignments) < PredictableBelowAssignments;

    /// <summary>Null when a valid assignment exists, otherwise why not.</summary>
    public static string? Problem(DrawRules rules)
    {
        var graph = new Graph(rules);
        if (graph.N < MinPeople)
            return $"The hat needs at least {MinPeople} people.";
        return graph.CanComplete(new int[graph.N].Fill(-1)) ? null : WhyImpossible(rules, graph);
    }

    /// <summary>Names the rule that makes the draw impossible, where that can be told.</summary>
    static string WhyImpossible(DrawRules rules, Graph graph)
    {
        for (var g = 0; g < graph.N; g++)
            if (!Enumerable.Range(0, graph.N).Any(r => graph.Allowed[g, r]))
                return $"{rules.Name(graph.People[g])} can't draw anyone under these rules"
                    + (rules.Forbidden.Any(f => f.Giver == graph.People[g]) ? ", counting who they drew in earlier years." : ".");
        for (var r = 0; r < graph.N; r++)
            if (!Enumerable.Range(0, graph.N).Any(g => graph.Allowed[g, r]))
                return $"Nobody can draw {rules.Name(graph.People[r])} under these rules"
                    + (rules.Forbidden.Any(f => f.Recipient == graph.People[r]) ? ", counting who drew them in earlier years." : ".");

        // Household members all draw outside it, so a household needs at least as many people
        // outside it as in it.
        var biggest = rules.People.Where(rules.HouseholdOf.ContainsKey)
            .GroupBy(p => rules.HouseholdOf[p])
            .MaxBy(h => h.Count());
        if (biggest is not null && biggest.Count() * 2 > graph.N)
            return $"The household {rules.Name(biggest.Key)} has more than half the people in the hat, so they can't all draw someone outside it.";

        return rules.Forbidden.Count > 0
            ? "There's no way to draw names under these rules. Loosen a household or a rule, or repeat pairings from fewer years back."
            : "There's no way to draw names under these rules. Loosen a household or a rule.";
    }

    static int[] Fill(this int[] array, int value)
    {
        Array.Fill(array, value);
        return array;
    }

    /// <summary>The allowed-pairs graph, by index into <see cref="People"/>.</summary>
    sealed class Graph
    {
        public readonly Guid[] People;
        public readonly int N;
        public readonly bool[,] Allowed;

        public Graph(DrawRules rules)
        {
            People = [.. rules.People.Distinct()];
            N = People.Length;
            Allowed = new bool[N, N];
            for (var g = 0; g < N; g++)
                for (var r = 0; r < N; r++)
                    Allowed[g, r] = rules.Allows(People[g], People[r]);
        }

        /// <summary>
        /// Every valid assignment (recipient index per giver), up to <paramref name="limit"/>.
        /// Each branch is checked for a completion first, so the search never wanders into dead
        /// ends and stops quickly once the limit is reached.
        /// </summary>
        public IEnumerable<int[]> Assignments(int limit)
        {
            var count = 0;
            var current = new int[N].Fill(-1);
            var used = new bool[N];
            var stack = new Stack<(int Giver, int Next)>();
            if (N == 0 || !CanComplete(current))
                yield break;
            stack.Push((0, 0));
            while (stack.Count > 0 && count < limit)
            {
                var (g, next) = stack.Pop();
                if (current[g] >= 0)
                {
                    used[current[g]] = false;
                    current[g] = -1;
                }
                var r = next;
                for (; r < N; r++)
                {
                    if (!Allowed[g, r] || used[r])
                        continue;
                    current[g] = r;
                    used[r] = true;
                    if (CanComplete(current))
                        break;
                    current[g] = -1;
                    used[r] = false;
                }
                if (r == N)
                    continue;
                stack.Push((g, r + 1));
                if (g == N - 1)
                {
                    count++;
                    yield return (int[])current.Clone();
                }
                else
                    stack.Push((g + 1, 0));
            }
        }

        /// <summary>
        /// One attempt at a single loop: a random walk from a random start through people not
        /// yet drawn, closing back to the start. Null if it gets stuck.
        /// </summary>
        public int[]? TrySingleCycle()
        {
            var result = new int[N].Fill(-1);
            var visited = new bool[N];
            var start = RandomNumberGenerator.GetInt32(N);
            var at = start;
            visited[at] = true;
            var candidates = new List<int>(N);
            for (var step = 1; step < N; step++)
            {
                candidates.Clear();
                for (var r = 0; r < N; r++)
                    if (!visited[r] && Allowed[at, r])
                        candidates.Add(r);
                if (candidates.Count == 0)
                    return null;
                var next = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
                result[at] = next;
                visited[next] = true;
                at = next;
            }
            if (!Allowed[at, start])
                return null;
            result[at] = start;
            return result;
        }

        /// <summary>
        /// Any valid assignment, built giver by giver in random order with candidates tried in
        /// random order. A candidate is kept only if the rest can still be completed, so every
        /// pair that is part of some valid assignment can come up.
        /// </summary>
        public int[] RandomAssignment()
        {
            var result = new int[N].Fill(-1);
            var givers = Shuffled(N);
            foreach (var g in givers)
            {
                foreach (var r in Shuffled(N))
                {
                    if (!Allowed[g, r] || Array.IndexOf(result, r) >= 0)
                        continue;
                    result[g] = r;
                    if (CanComplete(result))
                        break;
                    result[g] = -1;
                }
                if (result[g] < 0)
                    throw new InvalidOperationException("The draw lost its completion."); // can't happen: checked before
            }
            return result;
        }

        /// <summary>
        /// Whether the givers without a recipient yet (-1) can each get a distinct recipient not
        /// yet drawn: a perfect matching on what's left (Kuhn's augmenting paths).
        /// </summary>
        public bool CanComplete(int[] partial)
        {
            var taken = new bool[N];
            foreach (var r in partial)
                if (r >= 0)
                    taken[r] = true;
            var matchOf = new int[N].Fill(-1); // recipient → giver
            for (var g = 0; g < N; g++)
            {
                if (partial[g] >= 0)
                    continue;
                if (!Augment(g, new bool[N], taken, matchOf))
                    return false;
            }
            return true;
        }

        bool Augment(int g, bool[] seen, bool[] taken, int[] matchOf)
        {
            for (var r = 0; r < N; r++)
            {
                if (!Allowed[g, r] || taken[r] || seen[r])
                    continue;
                seen[r] = true;
                if (matchOf[r] < 0 || Augment(matchOf[r], seen, taken, matchOf))
                {
                    matchOf[r] = g;
                    return true;
                }
            }
            return false;
        }

        static int[] Shuffled(int n)
        {
            var items = Enumerable.Range(0, n).ToArray();
            RandomNumberGenerator.Shuffle(items.AsSpan());
            return items;
        }
    }
}
