namespace Pollyanna.Auth;

public static class ProviderIconEndpoint
{
    public const string PathPrefix = "/auth/icon/";

    /// <summary>
    /// <c>GET /auth/icon/{key}</c>: a provider's logo, for the sign-in buttons (spec §3). Anyone may
    /// fetch it, since the chooser is shown signed out. Pollyanna serves the file itself, with its
    /// checked content type and <c>nosniff</c>, so the strict CSP holds. A provider without an icon,
    /// or an unknown key, is 404.
    /// </summary>
    public static void MapProviderIcons(this IEndpointRouteBuilder app) =>
        app.MapGet(PathPrefix + "{key}", (string key, HttpContext http, ProviderRegistry providers) =>
        {
            if (providers.ByKey(key)?.Icon is not { } icon)
                return Results.NotFound();

            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            // An SVG opened on its own runs no script and loads nothing.
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            // The buttons link to a versioned URL (Provider.IconPath), so a changed file gets a new one.
            headers.CacheControl = "public, max-age=86400";
            return Results.Bytes(icon.Data, icon.ContentType);
        }).AllowAnonymous();
}
