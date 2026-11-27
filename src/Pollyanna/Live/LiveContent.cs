using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pollyanna.Auth;
using Pollyanna.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Live;

/// <summary>
/// The content of a giver-recipient happening (spec §16 "Who a live signal goes to"): the pages it
/// touches, rendered for that one person here in the server, as a live refetch of each would come
/// back, and sent down their connection. Nothing goes over the wire to ask for them, so no
/// request follows the other side's post in the proxy log, and nothing here logs.
/// <para>
/// A connection doesn't know which page it's on (its path names none), so the frame carries every
/// page the happening can change, by path: home, the event page and the questions page. The page
/// that matches patches its regions; any other takes only the masthead's "What's new" rows.
/// </para>
/// <para>
/// Each page is the app's own Razor page, run in memory with the live refetch header, so it is
/// read-only (no marker moves, no stored row goes, spec §16) and comes out exactly as a reload
/// would. Form tokens are left empty: one made here wouldn't match the person's cookie, so the
/// page's script fills in its own.
/// </para>
/// </summary>
public sealed partial class LiveContent(IServiceScopeFactory scopes, EndpointDataSource endpoints)
{
    /// <summary>The content frame for <paramref name="person"/> about <paramref name="area"/> in the event.</summary>
    public async Task<LiveFrame> FrameAsync(Guid person, Guid eventId, string area)
    {
        var pages = new Dictionary<string, string>();
        foreach (var (path, page, values) in new[]
        {
            ("/", "/Index", new RouteValueDictionary()),
            (LiveShape.EventPath(eventId), "/Events/Event", new RouteValueDictionary { ["id"] = eventId.ToString() }),
            (LiveShape.QuestionsPath(eventId), "/Events/Questions", new RouteValueDictionary { ["id"] = eventId.ToString() }),
        })
            if (await RenderAsync(person, path, page, values) is { } html)
                pages[path] = html;
        return new LiveFrame("content", JsonSerializer.Serialize(new { @event = eventId, area, pages }));
    }

    /// <summary>One page as <paramref name="person"/> would get it on a live refetch; null unless it's there for them (200).</summary>
    async Task<string?> RenderAsync(Guid person, string path, string page, RouteValueDictionary values)
    {
        var endpoint = endpoints.Endpoints.OfType<RouteEndpoint>()
            .FirstOrDefault(e => e.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath == page);
        if (endpoint?.RequestDelegate is null)
            return null;

        await using var scope = scopes.CreateAsyncScope();
        var name = await scope.ServiceProvider.GetRequiredService<PollyannaDb>().People
            .Where(p => p.Id == person).Select(p => p.DisplayName).SingleOrDefaultAsync();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SessionAuthenticationHandler.PersonIdClaim, person.ToString()), new Claim(ClaimTypes.Name, name ?? "")],
            SessionAuthenticationHandler.SchemeName, ClaimTypes.Name, ClaimTypes.Role));
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;
        context.Request.Headers[LiveRefetch.Header] = LiveRefetch.CatchUp;
        values["page"] = page;
        context.Request.RouteValues = values;
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.SetEndpoint(endpoint);

        await endpoint.RequestDelegate(context);
        if (context.Response.StatusCode != StatusCodes.Status200OK)
            return null;
        return Token().Replace(Encoding.UTF8.GetString(body.ToArray()), "$1\"\"");
    }

    [GeneratedRegex("(name=\"__RequestVerificationToken\" type=\"hidden\" value=)\"[^\"]*\"")]
    private static partial Regex Token();
}
