using System.Collections.Concurrent;
using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pollyanna.Live;
using Microsoft.Extensions.DependencyInjection;

namespace Pollyanna.Tests.Support;

/// <summary>One frame of the live stream as a reader got it: its event type and data.</summary>
public sealed record LiveFrame(string Type, string Data);

/// <summary>A hint frame's data (spec §16): the event and the area, never a person.</summary>
public sealed record LiveHintData(Guid Event, string Area);

/// <summary>
/// A content frame's data (spec §16, giver-recipient happenings): the event, the area (the
/// happening's kind) and the pages it touches as that one person would see them, by path, so
/// their open page patches itself with no follow-up request.
/// </summary>
public sealed record LiveContentData(Guid Event, string Area, Dictionary<string, string> Pages);

/// <summary>
/// <c>app.Live</c>: every live stream the test opened, and <see cref="SettleAsync"/>, which sends a
/// marker down every open connection and waits for each reader to get it, so a test proves that
/// nothing else arrived, without a timeout (spec §16 Testing).
/// </summary>
public sealed class TestLive(Func<IServiceProvider> services)
{
    readonly ConcurrentQueue<LiveReader> readers = new();
    int markers;

    internal void Add(LiveReader reader) => readers.Enqueue(reader);

    public async Task SettleAsync()
    {
        var marker = Interlocked.Increment(ref markers).ToString();
        services().GetRequiredService<LiveConnections>().Mark(marker);
        await Task.WhenAll(readers.Select(r => r.WaitForAsync(marker)));
    }
}

/// <summary>
/// One open <c>/live</c> stream, read with <see cref="SseParser"/> in the background. Holds the
/// frames received so far, leaving out the stream's opening frame and the test's markers.
/// </summary>
public sealed class LiveReader : IDisposable
{
    readonly HttpResponseMessage response;
    readonly CancellationTokenSource stop = new();
    readonly List<LiveFrame> frames = [];
    readonly List<string> markers = [];
    readonly TaskCompletionSource<string> hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Task pump;
    event Action? Changed;

    LiveReader(HttpResponseMessage response)
    {
        this.response = response;
        pump = Task.Run(PumpAsync);
    }

    /// <summary>Opens <c>/live</c> with <paramref name="client"/>'s cookies and waits for its opening frame.</summary>
    internal static async Task<LiveReader> OpenAsync(HttpClient client, TestLive live)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/live"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reader = new LiveReader(response);
        live.Add(reader);
        reader.Tab = await reader.hello.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return reader;
    }

    public HttpResponseMessage Response => response;

    /// <summary>This connection's own id, from its opening frame; posts name it to leave this tab out of "your other tabs".</summary>
    public string Tab { get; private set; } = "";

    /// <summary>Whether the server has ended the stream.</summary>
    public bool Ended => ended;
    volatile bool ended;

    /// <summary>Completes once the server has ended the stream.</summary>
    public Task EndedAsync() => pump.WaitAsync(TimeSpan.FromSeconds(10));

    public IReadOnlyList<LiveFrame> Frames { get { lock (frames) return [.. frames]; } }

    public IReadOnlyList<LiveHintData> Hints =>
        [.. Frames.Where(f => f.Type == "hint").Select(f => JsonSerializer.Deserialize<LiveHintData>(f.Data, JsonSerializerOptions.Web)!)];

    public IReadOnlyList<LiveContentData> Contents =>
        [.. Frames.Where(f => f.Type == "content").Select(f => JsonSerializer.Deserialize<LiveContentData>(f.Data, JsonSerializerOptions.Web)!)];

    /// <summary>
    /// Every frame as one string each, for the secrecy runs to compare: the event's ID and the
    /// times taken out (each run starts later on the clock), content frames' pages laid out by path.
    /// </summary>
    public List<string> Script(Guid eventId)
    {
        string Plain(string s) => Regex.Replace(s, "<time datetime=\"[^\"]*\"[^>]*>[^<]*</time>", "<time/>").Replace(eventId.ToString(), "{event}");
        return [.. Frames.Select(f => f.Type == "content"
            ? Plain(string.Join("\n", JsonSerializer.Deserialize<LiveContentData>(f.Data, JsonSerializerOptions.Web)! is var c
                ? new[] { $"content {c.Area}" }.Concat(c.Pages.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}\n{p.Value}"))
                : []))
            : Plain($"{f.Type} {f.Data}"))];
    }

    /// <summary>Forgets the frames received so far, e.g. after setting up.</summary>
    public void Clear() { lock (frames) frames.Clear(); }

    internal Task WaitForAsync(string marker)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            bool seen;
            lock (frames) seen = markers.Contains(marker);
            if (seen || ended) done.TrySetResult();
        }
        Changed += Check;
        Check();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(t => { Changed -= Check; t.GetAwaiter().GetResult(); });
    }

    async Task PumpAsync()
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(stop.Token))
            {
                lock (frames)
                {
                    if (item.EventType == "hello") hello.TrySetResult(item.Data);
                    else if (item.EventType == "settle") markers.Add(item.Data);
                    else frames.Add(new LiveFrame(item.EventType, item.Data));
                }
                Changed?.Invoke();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException) { }
        finally
        {
            hello.TrySetException(new InvalidOperationException("The live stream ended before its opening frame."));
            ended = true;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        response.Dispose();
    }
}
