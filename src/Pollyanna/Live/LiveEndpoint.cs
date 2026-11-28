using System.Text;
using Pollyanna.Auth;
using Pollyanna.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Live;

/// <summary>
/// <c>GET /live</c> (spec §16): one server-sent events stream per open page, the same path for
/// every page, so a log line says "online", not where. It is written by hand rather than through
/// <c>TypedResults.ServerSentEvents</c>, which has no way to write the keep-alive comment; the
/// headers that result would set are set here.
/// </summary>
public static class LiveEndpoint
{
    /// <summary>A comment this often keeps proxies from closing a quiet stream (spec §16). A constant, not a setting.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static void MapLive(this IEndpointRouteBuilder app) =>
        // Anonymous so a missing session gets a status: the usual redirect to /auth/login would
        // end an EventSource for good, and silently.
        app.MapGet("/live", StreamAsync).AllowAnonymous();

    static async Task StreamAsync(HttpContext context, LiveConnections connections, TimeProvider clock,
        IServiceScopeFactory scopes, IHostApplicationLifetime lifetime)
    {
        if (context.User.Identity?.IsAuthenticated != true || context.User.SessionId() is not { } session)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache,no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.ContentEncoding = "identity";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        var connection = connections.Open(context.User.PersonId(), session);
        try
        {
            // Started before the opening frame, so a test that moves the clock once it has that
            // frame is sure to reach this timer.
            var tick = Task.Delay(KeepAlive, clock, stop.Token);
            await response.StartAsync(stop.Token);
            await WriteAsync(response, new LiveFrame("hello", connection.Tab), stop.Token);
            var waiting = connection.Frames.WaitToReadAsync(stop.Token).AsTask();
            while (true)
            {
                if (await Task.WhenAny(waiting, tick) == tick)
                {
                    await tick;
                    if (!await SessionLivesAsync(scopes, clock, session, stop.Token))
                        break;
                    await response.Body.WriteAsync(": keep-alive\n\n"u8.ToArray(), stop.Token);
                    await response.Body.FlushAsync(stop.Token);
                    tick = Task.Delay(KeepAlive, clock, stop.Token);
                    continue;
                }
                if (!await waiting)
                    break;
                while (connection.Frames.TryRead(out var frame))
                    await WriteAsync(response, frame, stop.Token);
                waiting = connection.Frames.WaitToReadAsync(stop.Token).AsTask();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            connections.Close(connection);
        }
    }

    static async Task WriteAsync(HttpResponse response, LiveFrame frame, CancellationToken cancellationToken)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes($"event: {frame.Type}\ndata: {frame.Data}\n\n"), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>The stream was authenticated once, when it opened; each keep-alive checks the session still holds.</summary>
    static async Task<bool> SessionLivesAsync(IServiceScopeFactory scopes, TimeProvider clock, string session, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PollyannaDb>();
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.Sessions.AnyAsync(s => s.Id == session && s.ExpiresAt > now, cancellationToken);
    }
}
