using Pollyanna.Data;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Pollyanna.Live;

/// <summary>
/// A live page's shape (spec §16 "What each page does live"): the layout writes it on
/// <c>main</c> as <c>data-live-shape</c>, with <c>data-live-line</c>, the quiet line for arriving
/// at it. When a live refetch comes back with another shape, it's a state change: the script
/// swaps the page body, or, with something being edited, shows the line with "Show".
/// </summary>
public static class LiveShape
{
    /// <summary>The line for arriving at <paramref name="ev"/>'s status; null for a draft, which nobody arrives at.</summary>
    public static string? Line(Event ev) => ev.Status switch
    {
        EventStatus.Open => "The event is open.",
        EventStatus.Drawn => ev.Redrawn ? "Names have been drawn again." : "Names have been drawn.",
        EventStatus.Revealed => "Everyone's been revealed.",
        EventStatus.Archived => "This event was archived.",
        _ => null,
    };

    /// <summary>Marks a page of one event as live: <paramref name="page"/> ("manage") and the status name its shape.</summary>
    public static void Mark(ViewDataDictionary viewData, string page, Event ev)
    {
        viewData["LiveShape"] = $"{page}-{ev.Status.ToString().ToLowerInvariant()}";
        viewData["LiveLine"] = Line(ev);
    }
}
