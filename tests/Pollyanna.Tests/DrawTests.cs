using Pollyanna.Events;

namespace Pollyanna.Tests;

/// <summary>
/// The draw algorithm (spec §5.1, §9): every constraint, impossibility detection, reachability of
/// every valid recipient, the single-loop preference and the predictability thresholds.
/// </summary>
public sealed class DrawTests
{
    static Guid[] People(int n) => [.. Enumerable.Range(0, n).Select(_ => Guid.NewGuid())];

    static DrawRules Rules(Guid[] people,
        IEnumerable<Guid[]>? households = null,
        IEnumerable<(Guid, Guid)>? exclusions = null,
        IEnumerable<(Guid, Guid)>? forbidden = null)
    {
        var householdOf = new Dictionary<Guid, Guid>();
        var names = people.Select((p, i) => (p, $"P{i}")).ToDictionary(x => x.p, x => x.Item2);
        foreach (var members in households ?? [])
        {
            var id = Guid.NewGuid();
            names[id] = "The Crowd";
            foreach (var m in members)
                householdOf[m] = id;
        }
        return new DrawRules(people, householdOf, [.. exclusions ?? []], [.. forbidden ?? []]) { Names = names };
    }

    /// <summary>Every valid assignment, by trying every permutation.</summary>
    static List<Dictionary<Guid, Guid>> BruteForce(DrawRules rules)
    {
        var people = rules.People.ToArray();
        var found = new List<Dictionary<Guid, Guid>>();
        void Permute(int[] order, int k)
        {
            if (k == order.Length)
            {
                if (Enumerable.Range(0, order.Length).All(i => rules.Allows(people[i], people[order[i]])))
                    found.Add(Enumerable.Range(0, order.Length).ToDictionary(i => people[i], i => people[order[i]]));
                return;
            }
            for (var i = k; i < order.Length; i++)
            {
                (order[k], order[i]) = (order[i], order[k]);
                Permute(order, k + 1);
                (order[k], order[i]) = (order[i], order[k]);
            }
        }
        Permute([.. Enumerable.Range(0, people.Length)], 0);
        return found;
    }

    static string Key(IReadOnlyDictionary<Guid, Guid> pairs) =>
        string.Join(",", pairs.OrderBy(p => p.Key).Select(p => $"{p.Key}>{p.Value}"));

    static bool IsSingleLoop(IReadOnlyDictionary<Guid, Guid> pairs)
    {
        var start = pairs.Keys.First();
        var at = pairs[start];
        var steps = 1;
        while (at != start)
        {
            at = pairs[at];
            steps++;
        }
        return steps == pairs.Count;
    }

    static void AssertValid(DrawRules rules, DrawResult result)
    {
        Assert.True(result.Ok, result.Problem);
        var pairs = result.Pairs!;
        Assert.Equal(rules.People.Order(), pairs.Keys.Order());
        Assert.Equal(rules.People.Order(), pairs.Values.Order());
        Assert.All(pairs, p => Assert.True(rules.Allows(p.Key, p.Value)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(40)]
    public void Everyone_gives_once_receives_once_and_never_draws_themselves(int n)
    {
        var rules = Rules(People(n));
        for (var i = 0; i < 200; i++)
        {
            var result = Draw.Run(rules);
            AssertValid(rules, result);
            Assert.All(result.Pairs!, p => Assert.NotEqual(p.Key, p.Value));
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Nobody_draws_someone_in_their_own_household(int n)
    {
        var p = People(n);
        var households = new[] { new[] { p[0], p[1] }, new[] { p[2], p[3] } };
        var rules = Rules(p, households: households);
        for (var i = 0; i < 300; i++)
        {
            var result = Draw.Run(rules);
            AssertValid(rules, result);
            var pairs = result.Pairs!;
            Assert.NotEqual(p[1], pairs[p[0]]);
            Assert.NotEqual(p[0], pairs[p[1]]);
            Assert.NotEqual(p[3], pairs[p[2]]);
            Assert.NotEqual(p[2], pairs[p[3]]);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Nobody_draws_someone_they_share_an_exclusion_with_either_way(int n)
    {
        var p = People(n);
        var rules = Rules(p, exclusions: [(p[0], p[1]), (p[2], p[0])]);
        for (var i = 0; i < 300; i++)
        {
            var pairs = Draw.Run(rules).Pairs!;
            Assert.NotEqual(p[1], pairs[p[0]]);
            Assert.NotEqual(p[0], pairs[p[1]]);
            Assert.NotEqual(p[0], pairs[p[2]]);
            Assert.NotEqual(p[2], pairs[p[0]]);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Extra_forbidden_pairs_apply_one_way_only(int n)
    {
        // Where the no-repeat rule plugs in: a forbidden giver → recipient pair.
        var p = People(n);
        var rules = Rules(p, forbidden: [(p[0], p[1])]);
        var seenReverse = false;
        for (var i = 0; i < 400; i++)
        {
            var pairs = Draw.Run(rules).Pairs!;
            Assert.NotEqual(p[1], pairs[p[0]]);
            seenReverse |= pairs[p[1]] == p[0];
        }
        Assert.True(seenReverse, "P1 should still be able to draw P0");
    }

    [Fact]
    public void Fewer_than_three_people_cant_draw()
    {
        foreach (var n in new[] { 0, 1, 2 })
        {
            var result = Draw.Run(Rules(People(n)));
            Assert.False(result.Ok);
            Assert.Contains("at least 3", result.Problem);
        }
    }

    [Fact]
    public void A_household_with_more_than_half_the_people_is_named_as_the_problem()
    {
        var p = People(5);
        var rules = Rules(p, households: [[p[0], p[1], p[2]]]);
        var result = Draw.Run(rules);
        Assert.False(result.Ok);
        Assert.Null(result.Pairs);
        Assert.Contains("The household The Crowd has more than half", result.Problem);
        Assert.Equal(result.Problem, Draw.Problem(rules));
    }

    [Fact]
    public void Someone_who_can_draw_nobody_is_named()
    {
        var p = People(4);
        var result = Draw.Run(Rules(p, exclusions: [(p[2], p[0]), (p[2], p[1]), (p[2], p[3])]));
        Assert.False(result.Ok);
        Assert.Equal("P2 can't draw anyone under these rules.", result.Problem);
    }

    [Fact]
    public void Someone_nobody_can_draw_is_named()
    {
        var p = People(4);
        var result = Draw.Run(Rules(p, forbidden: [(p[0], p[3]), (p[1], p[3]), (p[2], p[3])]));
        Assert.False(result.Ok);
        Assert.Equal("Nobody can draw P3 under these rules, counting who drew them in earlier years.", result.Problem);
    }

    [Fact]
    public void Other_impossible_rules_get_a_general_message()
    {
        // P0, P1 and P2 may each only draw P3: everyone has someone, but not enough to go round.
        var p = People(4);
        var forbidden = new[] { 0, 1, 2 }.SelectMany(g => new[] { 0, 1, 2 }.Where(r => r != g).Select(r => (p[g], p[r])));
        var result = Draw.Run(Rules(p, forbidden: forbidden));
        Assert.False(result.Ok);
        Assert.StartsWith("There's no way to draw names under these rules.", result.Problem);
    }

    [Fact]
    public void Small_draws_pick_uniformly_among_every_valid_assignment()
    {
        // Under 6 people there is no single-loop preference: every valid assignment, loops of
        // two included, comes up about equally often.
        var p = People(5);
        var rules = Rules(p, households: [[p[0], p[1]]], exclusions: [(p[2], p[3])]);
        var valid = BruteForce(rules).Select(Key).ToHashSet();
        Assert.True(valid.Count > 5);

        const int draws = 6000;
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < draws; i++)
        {
            var key = Key(Draw.Run(rules).Pairs!);
            Assert.Contains(key, valid);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        Assert.Equal(valid.Count, counts.Count);
        var expected = (double)draws / valid.Count;
        Assert.All(counts.Values, c => Assert.InRange(c, expected * 0.7, expected * 1.3));
    }

    [Fact]
    public void Larger_draws_make_one_loop_and_every_valid_recipient_is_reachable()
    {
        var p = People(7);
        var rules = Rules(p, households: [[p[0], p[1]]], exclusions: [(p[2], p[3])]);
        // The pairs that appear in some valid single loop: every one of them should come up.
        var reachable = BruteForce(rules).Where(IsSingleLoop).SelectMany(a => a).ToHashSet();
        var seen = new HashSet<KeyValuePair<Guid, Guid>>();
        for (var i = 0; i < 4000; i++)
        {
            var result = Draw.Run(rules);
            AssertValid(rules, result);
            Assert.True(IsSingleLoop(result.Pairs!));
            seen.UnionWith(result.Pairs!);
        }
        Assert.Equal(reachable.Count, seen.Count);
        Assert.Subset(reachable, seen);
        // Without loop-breaking rules, that is every pair the rules allow.
        Assert.Equal(p.Sum(g => p.Count(r => rules.Allows(g, r))), seen.Count);
    }

    [Fact]
    public void Without_any_single_loop_the_draw_falls_back_to_any_valid_assignment()
    {
        // Two groups of three that may only draw within their group: no loop through all six.
        var p = People(6);
        var groups = new[] { p[..3], p[3..] };
        var forbidden = p.SelectMany(g => p.Where(r => groups.Single(x => x.Contains(g)).Contains(r) is false).Select(r => (g, r)));
        var rules = Rules(p, forbidden: forbidden);
        var valid = BruteForce(rules).Select(Key).ToHashSet();
        Assert.Equal(4, valid.Count);

        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var result = Draw.Run(rules);
            AssertValid(rules, result);
            Assert.False(IsSingleLoop(result.Pairs!));
            seen.Add(Key(result.Pairs!));
        }
        Assert.Equal(valid, seen);
    }

    [Fact]
    public void The_only_possible_assignment_is_found()
    {
        // Only pairs drawing each other are allowed.
        var p = People(6);
        var partner = new Dictionary<Guid, Guid> { [p[0]] = p[1], [p[1]] = p[0], [p[2]] = p[3], [p[3]] = p[2], [p[4]] = p[5], [p[5]] = p[4] };
        var rules = Rules(p, forbidden: p.SelectMany(g => p.Where(r => r != partner[g]).Select(r => (g, r))));
        Assert.Equal(partner, Draw.Run(rules).Pairs);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 9)]
    [InlineData(5, 44)]
    public void Counting_finds_every_valid_assignment(int n, int expected)
    {
        Assert.Equal(expected, Draw.Count(Rules(People(n)), int.MaxValue));
    }

    [Fact]
    public void Counting_stops_at_the_limit()
    {
        Assert.Equal(20, Draw.Count(Rules(People(30)), 20));
        Assert.Equal(1, Draw.Count(Rules(People(30)), 1));
    }

    [Fact]
    public void Counting_agrees_with_trying_every_permutation_under_rules()
    {
        var random = new Random(7);
        for (var round = 0; round < 40; round++)
        {
            var p = People(random.Next(3, 7));
            var forbidden = Enumerable.Range(0, random.Next(0, 10))
                .Select(_ => (p[random.Next(p.Length)], p[random.Next(p.Length)]));
            var rules = Rules(p, forbidden: forbidden);
            Assert.Equal(BruteForce(rules).Count, Draw.Count(rules, int.MaxValue));
        }
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(9, false)]
    public void The_predictability_warning_fires_below_five_people(int n, bool expected)
    {
        // With no rules, 5 people already have 44 valid assignments, so only the head count matters.
        Assert.Equal(expected, Draw.IsPredictable(Rules(People(n))));
    }

    [Fact]
    public void The_predictability_warning_fires_below_twenty_valid_assignments()
    {
        // Search for rule sets on 5 and 6 people right around the threshold.
        var random = new Random(11);
        var sawJustBelow = false;
        var sawAtThreshold = false;
        for (var round = 0; round < 4000 && !(sawJustBelow && sawAtThreshold); round++)
        {
            var p = People(random.Next(5, 7));
            var forbidden = Enumerable.Range(0, random.Next(1, 12))
                .Select(_ => (p[random.Next(p.Length)], p[random.Next(p.Length)]));
            var rules = Rules(p, forbidden: forbidden);
            var count = BruteForce(rules).Count;
            if (count == 0)
                continue;
            Assert.Equal(count < Draw.PredictableBelowAssignments, Draw.IsPredictable(rules));
            sawJustBelow |= count == Draw.PredictableBelowAssignments - 1;
            sawAtThreshold |= count == Draw.PredictableBelowAssignments;
        }
        Assert.True(sawJustBelow && sawAtThreshold, "the search should hit 19 and 20 valid assignments");
    }
}
