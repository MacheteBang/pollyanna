using Pollyanna.WhatsNew;

namespace Pollyanna.Pages.Shared;

/// <summary>
/// The masthead's bell and its "What's new" panel (spec §7, §12, §16): the person's rows, in
/// order, and the time now, for "Today, 7:42 PM". Every signed-in page renders the panel, hidden;
/// site.js only opens it and dismisses in place, so there is no separate fragment endpoint.
/// </summary>
/// <param name="Withheld">
/// The rows tied to an assignment are left out (a live refetch a hint set off, spec §16): the
/// script keeps the ones it has, and counts the bell from what it shows.
/// </param>
public sealed record WhatsNewPanelView(IReadOnlyList<WhatsNewRow> Rows, DateTime Now, bool Withheld = false)
{
    /// <summary>"bell", plus "bell-new" (filled) with something new.</summary>
    public string BellClass => Rows.Count > 0 ? "bell bell-new" : "bell";

    /// <summary>The bell's accessible label, the only place the rows are counted.</summary>
    public string BellLabel => Rows.Count switch
    {
        0 => "What's new, nothing new",
        1 => "What's new, 1 notification",
        var n => $"What's new, {n} notifications",
    };
}
