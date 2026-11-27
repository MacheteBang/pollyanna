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
    public string BellLabel => Label(Rows.Count);

    static string Label(int count) => count switch
    {
        0 => "What's new, nothing new",
        1 => "What's new, 1 notification",
        var n => $"What's new, {n} notifications",
    };

    /// <summary>The labels for none, one and many ("{n}" for the count), for the script to label the rows it shows.</summary>
    public static readonly string Labels = System.Text.Json.JsonSerializer.Serialize(new[] { Label(0), Label(1), Label(-1).Replace("-1", "{n}") });
}

/// <summary>
/// "What's new"'s rows as <c>_WhatsNewRows</c> renders them: in the bell's panel
/// (<paramref name="InPanel"/>, classes "bell-") or on the <c>/whats-new</c> page ("whats-new-").
/// </summary>
/// <param name="Withheld">The rows tied to an assignment are left out (a live refetch a hint set off).</param>
public sealed record WhatsNewRowsView(IReadOnlyList<WhatsNewRow> Rows, DateTime Now, bool InPanel, bool Withheld = false)
{
    public string Prefix => InPanel ? "bell" : "whats-new";
}
