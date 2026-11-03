using Pollyanna.Data;
using Pollyanna.Events;

namespace Pollyanna.Tests;

/// <summary>
/// What each event status allows, and for whom (spec §4, §5.4, §6). Every table has a column
/// per status, so a new status fails here until someone decides where it belongs.
/// </summary>
public sealed class EventLifecycleTests
{
    static readonly EventStatus[] Statuses = Enum.GetValues<EventStatus>();

    static readonly Dictionary<string, Func<Membership, bool>> Rules = new()
    {
        ["CanView"] = m => m.CanView,
        ["CanLeave"] = m => m.CanLeave,
        ["ThreadsReadable"] = m => m.ThreadsReadable,
        ["ShownToParticipants"] = m => m.ShownToParticipants,
        ["PeopleEditable"] = m => m.PeopleEditable,
        ["PeopleChangeable"] = m => m.PeopleChangeable,
        ["Editable"] = m => m.Editable,
        ["NamesDrawn"] = m => m.NamesDrawn,
        ["IsLive"] = m => m.IsLive,
        ["IsOver"] = m => m.IsOver,
    };

    static Membership As(EventStatus status, bool organizer, bool participant) =>
        new(new Event { Name = "Family gifts", Status = status }, organizer, participant);

    // Columns: draft, open, drawn, revealed, archived. "x" allows, "-" doesn't.
    [Theory]
    [InlineData("CanView", false, false, "-----")]
    [InlineData("CanView", false, true, "-xxxx")]
    [InlineData("CanView", true, false, "xxxxx")]
    [InlineData("CanView", true, true, "xxxxx")]
    [InlineData("CanLeave", false, false, "-----")]
    [InlineData("CanLeave", false, true, "-xx--")]
    [InlineData("CanLeave", true, false, "-----")]
    [InlineData("CanLeave", true, true, "xxx--")]
    [InlineData("ThreadsReadable", false, false, "-----")]
    [InlineData("ThreadsReadable", false, true, "--xxx")]
    [InlineData("ThreadsReadable", true, false, "-----")]
    [InlineData("ThreadsReadable", true, true, "--xxx")]
    [InlineData("ShownToParticipants", true, true, "-xxxx")]
    [InlineData("PeopleEditable", true, true, "xx---")]
    [InlineData("PeopleChangeable", true, true, "xxx--")]
    [InlineData("Editable", true, true, "xxxx-")]
    [InlineData("NamesDrawn", true, true, "--xxx")]
    [InlineData("IsLive", true, true, "--x--")]
    [InlineData("IsOver", true, true, "---xx")]
    public void Rule_allows_exactly_the_marked_statuses(string rule, bool organizer, bool participant, string allowed)
    {
        Assert.Equal(Statuses.Length, allowed.Length);
        var actual = new string([.. Statuses.Select(s => Rules[rule](As(s, organizer, participant)) ? 'x' : '-')]);
        Assert.Equal(allowed, actual);
    }

    [Fact]
    public void Status_only_rules_are_the_same_for_every_role()
    {
        foreach (var rule in new[] { "ShownToParticipants", "PeopleEditable", "PeopleChangeable", "Editable", "NamesDrawn", "IsLive", "IsOver" })
            foreach (var status in Statuses)
            {
                var answers = new[] { As(status, false, false), As(status, false, true), As(status, true, false), As(status, true, true) }
                    .Select(Rules[rule]).Distinct();
                Assert.Single(answers);
            }
    }

    const string AlreadyOpen = "This event is already open.";
    const string OpenFirst = "Open the event before drawing names.";
    const string DrawnAlready = "Names have been drawn already.";
    const string NotDrawn = "Names haven't been drawn.";
    const string RevealedAlready = "The pairings have been revealed already.";
    const string ArchivedAlready = "This event is archived already.";
    const string ArchiveNeedsDraw = "Only an event whose names are drawn can be archived.";

    // Columns: draft, open, drawn, revealed, archived. Null where the transition is allowed.
    public static TheoryData<string, EventStatus, string?[]> Transitions => new()
    {
        { "Open", EventStatus.Open, [null, AlreadyOpen, AlreadyOpen, AlreadyOpen, AlreadyOpen] },
        { "Draw", EventStatus.Drawn, [OpenFirst, null, DrawnAlready, DrawnAlready, DrawnAlready] },
        { "Redraw", EventStatus.Open, [NotDrawn, NotDrawn, null, NotDrawn, NotDrawn] },
        { "Reveal", EventStatus.Revealed, [NotDrawn, NotDrawn, null, RevealedAlready, RevealedAlready] },
        { "Archive", EventStatus.Archived, [ArchiveNeedsDraw, ArchiveNeedsDraw, null, null, ArchivedAlready] },
    };

    static Transition Named(string name) => name switch
    {
        "Open" => EventLifecycle.Open,
        "Draw" => EventLifecycle.Draw,
        "Redraw" => EventLifecycle.Redraw,
        "Reveal" => EventLifecycle.Reveal,
        "Archive" => EventLifecycle.Archive,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Transition_is_refused_from_every_status_but_its_own(string name, EventStatus to, string?[] refusals)
    {
        var transition = Named(name);
        Assert.Equal(to, transition.To);
        Assert.Equal(Statuses.Length, refusals.Length);
        for (var i = 0; i < Statuses.Length; i++)
        {
            Assert.Equal(refusals[i], transition.Refusal(Statuses[i]));
            Assert.Equal(refusals[i] is null, transition.From.Contains(Statuses[i]));
            Assert.Equal(refusals[i] is null, As(Statuses[i], true, false).Allows(transition));
        }
    }
}
