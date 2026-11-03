namespace Pollyanna.Tests.Support;

/// <summary>
/// Fixed times on tomorrow's date (UTC), for tests that set the fake clock to an exact time.
/// The clock starts at the real time and only moves forward, so a fixed date would pass.
/// </summary>
static class Tomorrow
{
    static readonly DateTime Day = DateTime.UtcNow.Date.AddDays(1);

    public static DateTime At(int hour, int minute = 0, int second = 0) => Day.Add(new TimeSpan(hour, minute, second));
}
