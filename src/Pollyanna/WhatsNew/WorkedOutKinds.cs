using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Happenings;

namespace Pollyanna.WhatsNew;

/// <summary>
/// A kind of "What's new" row worked out from a sealed row's markers: the worked-out column of its
/// row in <see cref="HappeningKinds.Table"/>.
/// </summary>
/// <param name="Marker">The marker dismissing its row moves (spec §16).</param>
/// <param name="TimelessRank">
/// Where its row goes among the kinds with no time, which come first in this order (spec §16);
/// null for a kind with a time.
/// </param>
public sealed record WorkedOutKind(SlipMarker Marker, int? TimelessRank = null);

/// <summary>
/// The five kinds tied to an assignment (spec §16, docs/adr/0008): never stored, worked out on
/// each request from the sealed row's markers (<see cref="SealedHat.NewsAsync"/>). The giver-side
/// lines never name the recipient and link to the plain event page, so the gift opens tied.
/// </summary>
public static class WorkedOutKinds
{
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
                ? new(ev.Id, HappeningKind.ThreadClosed, "Your recipient closed the conversation", page, ev.Name, null)
                : new(ev.Id, HappeningKind.ThreadReopened, "Your recipient reopened the conversation", page, ev.Name, null);
        if (news.Patched)
            yield return new(ev.Id, HappeningKind.Patched, "You're giving to someone new", page, ev.Name, null);
        if (news.NotesUpdated)
            yield return new(ev.Id, HappeningKind.NotesEdited, "Your recipient updated their notes", page, ev.Name, null);
    }
}
