using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Pollyanna.Live;

/// <summary>One frame for a live stream: its event type and its data, one line.</summary>
public sealed record LiveFrame(string Type, string Data)
{
    /// <summary>
    /// A content-free hint (spec §16): the event and the area, never a person. The page refetches,
    /// sending <c>refetch</c> as the <see cref="LiveRefetch.Header"/>: <see cref="LiveRefetch.CatchUp"/>
    /// when only the actor's own tabs hear of it (<paramref name="ownOnly"/>), so nobody else's
    /// refetch comes with it; otherwise <see cref="LiveRefetch.Hint"/>, which leaves the pair parts out.
    /// </summary>
    public static LiveFrame Hint(Guid eventId, string area, bool ownOnly) =>
        new("hint", JsonSerializer.Serialize(new { @event = eventId, area, refetch = ownOnly ? LiveRefetch.CatchUp : LiveRefetch.Hint }));
}

/// <summary>One open <c>/live</c> stream: whose it is, which session opened it, and its frames waiting to be written.</summary>
public sealed class LiveConnection
{
    internal LiveConnection(Guid person, string session)
    {
        Person = person;
        Session = session;
    }

    /// <summary>This connection's own id, sent in its opening frame so its page can name it ("not this tab").</summary>
    public string Tab { get; } = Guid.NewGuid().ToString("N");
    public Guid Person { get; }
    public string Session { get; }

    readonly Channel<LiveFrame> frames = Channel.CreateUnbounded<LiveFrame>(new() { SingleReader = true });
    public ChannelReader<LiveFrame> Frames => frames.Reader;

    internal void Send(LiveFrame frame) => frames.Writer.TryWrite(frame);

    /// <summary>Ends the stream once what's already queued is written.</summary>
    internal void End() => frames.Writer.TryComplete();
}

/// <summary>
/// Every open live connection, in memory (spec §16): one instance, so a singleton is enough.
/// Connections are known by person (and the session that opened them) only, never by assignment
/// or page; who an event's hint reaches is worked out when it's sent (<see cref="LiveSignals"/>).
/// Nothing here logs.
/// </summary>
public sealed class LiveConnections
{
    readonly ConcurrentDictionary<LiveConnection, byte> open = new();

    public LiveConnection Open(Guid person, string session)
    {
        var connection = new LiveConnection(person, session);
        open[connection] = 0;
        return connection;
    }

    public void Close(LiveConnection connection)
    {
        open.TryRemove(connection, out _);
        connection.End();
    }

    /// <summary>Sends <paramref name="frame"/> to every connection of <paramref name="people"/>, except the tab <paramref name="exceptTab"/>.</summary>
    public void Send(IEnumerable<Guid> people, LiveFrame frame, string? exceptTab = null)
    {
        var to = people.ToHashSet();
        foreach (var connection in open.Keys)
            if (to.Contains(connection.Person) && connection.Tab != exceptTab)
                connection.Send(frame);
    }

    /// <summary>Everyone with a connection open now: whom a giver-recipient happening's content could go to, worked out when it's sent.</summary>
    public IReadOnlyCollection<Guid> People => open.Keys.Select(c => c.Person).ToHashSet();

    /// <summary>Ends the streams a session opened, at sign-out.</summary>
    public void EndSession(string session)
    {
        foreach (var connection in open.Keys.Where(c => c.Session == session))
            Close(connection);
    }

    /// <summary>Ends every stream of a person, when their account is deleted.</summary>
    public void EndPerson(Guid person)
    {
        foreach (var connection in open.Keys.Where(c => c.Person == person))
            Close(connection);
    }

    /// <summary>The test hook (spec §16 Testing): a marker frame down every open connection.</summary>
    public void Mark(string marker)
    {
        foreach (var connection in open.Keys)
            connection.Send(new LiveFrame("settle", marker));
    }
}
