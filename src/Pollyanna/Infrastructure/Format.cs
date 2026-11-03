using System.Globalization;
using Pollyanna.Data;

namespace Pollyanna.Infrastructure;

/// <summary>The one fixed English format for dates and labels (spec §1).</summary>
public static class Format
{
    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    public static string Stamp(EventStatus status) => status.ToString().ToUpperInvariant();

    /// <summary>The date as <c>&lt;time datetime&gt;</c> wants it.</summary>
    public static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A message time (UTC, on the hour) as <c>&lt;time datetime&gt;</c> wants it.</summary>
    public static string IsoHour(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH':00:00Z'", CultureInfo.InvariantCulture);

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
