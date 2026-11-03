using Pollyanna.Configuration;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Pollyanna.Infrastructure;

public static class Middleware
{
    /// <summary>
    /// Pollyanna runs behind a TLS-terminating proxy (spec §2), so requests arrive as plain HTTP.
    /// The configured public URL, not the request or any forwarded header, says which scheme the
    /// browser is really using.
    /// </summary>
    public static IApplicationBuilder UsePublicScheme(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var scheme = new Lazy<string>(() => new Uri(services.GetRequiredService<PollyannaSettings>().PublicUrl).Scheme);
        return app.Use((context, next) =>
        {
            context.Request.Scheme = scheme.Value;
            return next(context);
        });
    }

    /// <summary>Security headers on every response (spec §8).</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XContentTypeOptions = "nosniff";
            return next(context);
        });

    /// <summary>
    /// Access log with method, route pattern and status only (spec §8): never the path itself,
    /// which may contain event, assignment or message IDs. Successful health checks are left out:
    /// the container's health check would otherwise add a line every 30 seconds.
    /// </summary>
    public static IApplicationBuilder UseAccessLog(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pollyanna.Access");
        return app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            finally
            {
                var route = context.GetEndpoint() switch
                {
                    RouteEndpoint r => "/" + (r.RoutePattern.RawText ?? RoutePatternText(r.RoutePattern)).TrimStart('/'),
                    { } e => e.DisplayName ?? "(endpoint)",
                    // The OIDC handler answers its callback itself, before routing picks an endpoint.
                    null when context.Request.Path == PollyannaSettings.CallbackPath => PollyannaSettings.CallbackPath,
                    null => context.Response.StatusCode == StatusCodes.Status404NotFound ? "(no route)" : "(static)",
                };
                if (route != "/healthz" || context.Response.StatusCode != StatusCodes.Status200OK)
                    logger.LogInformation("{Method} {Route} {Status}", context.Request.Method, route, context.Response.StatusCode);
            }
        });
    }

    static string RoutePatternText(RoutePattern pattern) =>
        string.Join('/', pattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p switch
        {
            RoutePatternLiteralPart l => l.Content,
            RoutePatternParameterPart pp => "{" + pp.Name + "}",
            _ => "",
        }))));
}
