using System.Globalization;
using Pollyanna.Data;
using Pollyanna.Events;

namespace Pollyanna.Infrastructure;

/// <summary>The one fixed English format for dates and labels (spec §1).</summary>
public static class Format
{
    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>"December 2025", naming an earlier event beside its name.</summary>
    public static string MonthYear(DateOnly date) => date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>An event's status as the small line under its date says it (spec §12 Statuses).</summary>
    public static string StatusLine(EventStatus status) => status switch
    {
        EventStatus.Draft => "draft, only organizers can see it",
        EventStatus.Open => "names not drawn yet",
        EventStatus.Drawn => "names drawn",
        EventStatus.Revealed => "everyone's been revealed",
        EventStatus.Archived => "over",
        _ => "",
    };

    /// <summary>The date as <c>&lt;time datetime&gt;</c> wants it.</summary>
    public static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A message time (UTC) to the minute, as <c>&lt;time datetime&gt;</c> wants it.</summary>
    public static string IsoMinute(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH':'mm'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A message's time in UTC, for the page without the script: "7:42 PM UTC", or "about 7 PM
    /// UTC" for one stored to the hour before times were exact (spec §6).
    /// </summary>
    public static string Said(Note message)
    {
        var at = message.CreatedAt;
        var hour = at.Hour % 12 == 0 ? 12 : at.Hour % 12;
        var half = at.Hour < 12 ? "AM" : "PM";
        if (!message.OnTheHour)
            return $"{hour}:{at.Minute:00} {half} UTC";
        return "about " + (at.Hour == 0 ? "midnight" : at.Hour == 12 ? "noon" : $"{hour} {half}") + " UTC";
    }

    public static string People(int count) => count == 1 ? "1 person" : $"{count} people";

    public static string Invites(int count) => count == 1 ? "1 invite pending" : $"{count} invites pending";

    /// <summary>"Carol's", "James'": for "Carol's notes".</summary>
    public static string Possessive(string name) =>
        name.EndsWith('s') || name.EndsWith('S') ? name + "'" : name + "'s";

    /// <summary>Up to two initials, for people without a picture.</summary>
    public static string Initials(string? name)
    {
        var words = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return initials.Length > 0 ? initials : "?";
    }
}
