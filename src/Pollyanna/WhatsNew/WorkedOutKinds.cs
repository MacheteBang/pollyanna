using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Happenings;

namespace Pollyanna.WhatsNew;

/// <summary>
/// The five kinds tied to an assignment (spec §16, docs/adr/0008): never stored, worked out on
/// each request from the sealed row's markers (<see cref="SealedHat.NewsAsync"/>). The giver-side
/// lines never name the recipient and link to the plain event page, so the gift opens tied.
/// </summary>
public static class WorkedOutKinds
{
    /// <summary>The marker dismissing a row of <paramref name="kind"/> moves; null if it isn't one of these.</summary>
    public static SlipMarker? MarkerOf(HappeningKind kind) => kind switch
    {
        HappeningKind.Question => SlipMarker.Question,
        HappeningKind.Reply => SlipMarker.Reply,
        HappeningKind.ConversationClosed or HappeningKind.ConversationReopened => SlipMarker.ThreadChanged,
        HappeningKind.Patched => SlipMarker.Patched,
        HappeningKind.NotesEdited => SlipMarker.NotesUpdated,
        _ => null,
    };

    /// <summary>
    /// Where a row of <paramref name="kind"/> goes among the kinds with no time, which come first
    /// in this order (spec §16); null for a kind with a time.
    /// </summary>
    public static int? TimelessRank(HappeningKind kind) => kind switch
    {
        HappeningKind.Patched => 0,
        HappeningKind.NotesEdited => 1,
        HappeningKind.ConversationClosed or HappeningKind.ConversationReopened => 2,
        _ => null,
    };

    /// <summary>The rows <paramref name="news"/> makes for one event, at most one per kind.</summary>
    public static IEnumerable<WhatsNewRow> Rows(Event ev, SlipNews news)
    {
        var page = $"/events/{ev.Id}";
        if (news.Question is { } asked)
            yield return new(ev.Id, HappeningKind.Question, "Your giver asked you something", $"{page}/questions", ev.Name, asked, news.QuestionUpto);
        if (news.Reply is { } replied)
            yield return new(ev.Id, HappeningKind.Reply, "Your recipient replied", page, ev.Name, replied, news.ReplyUpto);
        if (news.ThreadChanged)
            yield return news.Closed
                ? new(ev.Id, HappeningKind.ConversationClosed, "Your recipient closed the conversation", page, ev.Name, null)
                : new(ev.Id, HappeningKind.ConversationReopened, "Your recipient reopened the conversation", page, ev.Name, null);
        if (news.Patched)
            yield return new(ev.Id, HappeningKind.Patched, "You're giving to someone new", page, ev.Name, null);
        if (news.NotesUpdated)
            yield return new(ev.Id, HappeningKind.NotesEdited, "Your recipient updated their notes", page, ev.Name, null);
    }
}
