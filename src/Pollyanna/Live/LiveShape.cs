using System.Text.Json;
using Pollyanna.Data;
using Pollyanna.Happenings;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Pollyanna.Live;

/// <summary>
/// A page that updates live (spec §16 "What each page does live"), as the layout writes it on
/// <c>main</c> for the script: its shape (<c>data-live-shape</c>), the quiet line for arriving at it
/// (<c>data-live-line</c>), its own path as content frames name it (<c>data-live-path</c>), and,
/// for a page that sends a refetch on elsewhere, where to (<c>data-live-moved</c>). When a live
/// refetch comes back with another shape, it's a state change: the script swaps the page body,
/// or, with something being edited, shows the line with "Show".
/// </summary>
public sealed record LivePage(string Shape, string? Line, string Path, string? Moved = null);

/// <summary>Marks the live pages, and holds the paths and words the script is given rather than spelling them itself.</summary>
public static class LiveShape
{
    const string Key = "LivePage";

    /// <summary>The event page's path: what its live content is filed under, and where the questions page goes once revealed.</summary>
    public static string EventPath(Guid eventId) => $"/events/{eventId}";

    /// <summary>The questions page's path.</summary>
    public static string QuestionsPath(Guid eventId) => $"{EventPath(eventId)}/questions";

    /// <summary>The line for arriving at <paramref name="ev"/>'s status; null for a draft, which nobody arrives at.</summary>
    public static string? Line(Event ev) => Line(ev.Status, ev.Redrawn);

    static string? Line(EventStatus status, bool redrawn) => status switch
    {
        EventStatus.Open => "The event is open.",
        EventStatus.Drawn => redrawn ? "Names have been drawn again." : "Names have been drawn.",
        EventStatus.Revealed => "Everyone's been revealed.",
        EventStatus.Archived => "This event was archived.",
        _ => null,
    };

    /// <summary>
    /// The words and areas the script needs for what a refetch can't show by itself (spec §16):
    /// the lines for a page gone (canceled, taken out) or moved on (revealed, archived), and the
    /// hint areas that tell them apart and that bring a patched gift in again; and what a message
    /// box says when a send that stays on the page never reached the server. One JSON attribute.
    /// </summary>
    public static readonly string Words = JsonSerializer.Serialize(new
    {
        canceledArea = nameof(HappeningKind.EventCanceled),
        archivedArea = nameof(HappeningKind.EventArchived),
        patchedArea = nameof(HappeningKind.Patched),
        canceled = "This event was canceled",
        @out = "You're no longer in this event",
        back = "Back to your events",
        revealed = Line(EventStatus.Revealed, false),
        archived = Line(EventStatus.Archived, false),
        notSent = "Not sent. Check your connection and try again.",
    });

    /// <summary>Marks home as live: "home", or "home-empty" with no events.</summary>
    public static void MarkHome(ViewDataDictionary viewData, bool empty) =>
        viewData[Key] = new LivePage(empty ? "home-empty" : "home", null, "/");

    /// <summary>
    /// Marks a page of one event as live: <paramref name="page"/> ("event", "questions",
    /// "manage") and the status name its shape. The questions page sends a refetch on to the
    /// event page once names are revealed (spec §16).
    /// </summary>
    public static void Mark(ViewDataDictionary viewData, string page, Event ev) =>
        viewData[Key] = new LivePage($"{page}-{ev.Status.ToString().ToLowerInvariant()}", Line(ev), page switch
        {
            "event" => EventPath(ev.Id),
            "questions" => QuestionsPath(ev.Id),
            _ => $"{EventPath(ev.Id)}/{page}",
        }, page == "questions" ? EventPath(ev.Id) : null);

    /// <summary>The live page the view marked, if any.</summary>
    public static LivePage? Of(ViewDataDictionary viewData) => viewData[Key] as LivePage;
}
