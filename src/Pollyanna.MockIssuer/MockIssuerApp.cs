using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Pollyanna.MockIssuer;

/// <summary>A registered OIDC client.</summary>
public sealed class MockClient
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required IReadOnlyList<string> RedirectUris { get; init; }
}

public sealed class MockIssuerOptions
{
    /// <summary>The issuer identifier and base URL, e.g. <c>http://localhost:5021</c>.</summary>
    public required string Issuer { get; init; }
    public required IReadOnlyList<MockClient> Clients { get; init; }
    /// <summary>
    /// Advertise and serve a pushed authorization request endpoint (RFC 9126). Off by default, so
    /// Pollyanna sends ordinary authorization requests; on, a wrong client fails before the redirect.
    /// </summary>
    public bool PushedAuthorization { get; init; }
}

/// <summary>
/// What the person typed into the mock sign-in form. <see cref="EmailVerified"/> is null when the
/// <c>email_verified</c> claim should be left out entirely.
/// </summary>
public sealed record MockIdentity(string Sub, string? Name, string? Email, bool? EmailVerified, string? Picture);

/// <summary>
/// A minimal OpenID Connect provider for development and tests only: discovery, JWKS, the
/// authorization endpoint (a form instead of a login), the token endpoint with PKCE (S256) and
/// client authentication, userinfo, and optionally pushed authorization requests. State lives in
/// memory and the signing key is created on start.
/// </summary>
public static class MockIssuerApp
{
    public static WebApplication Build(MockIssuerOptions options, Action<WebApplicationBuilder>? configure = null, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IssuerState>();
        configure?.Invoke(builder);

        var app = builder.Build();
        var issuer = options.Issuer.TrimEnd('/');

        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>(Discovery())
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = issuer + "/authorize",
            ["token_endpoint"] = issuer + "/token",
            ["userinfo_endpoint"] = issuer + "/userinfo",
            ["jwks_uri"] = issuer + "/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = new[] { "query" },
            ["grant_types_supported"] = new[] { "authorization_code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["scopes_supported"] = new[] { "openid", "profile", "email" },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["claims_supported"] = new[] { "sub", "name", "email", "email_verified", "picture" },
        }));

        IEnumerable<KeyValuePair<string, object>> Discovery() => options.PushedAuthorization
            ? [new("pushed_authorization_request_endpoint", issuer + "/par")]
            : [];

        if (options.PushedAuthorization)
        {
            app.MapPost("/par", async (HttpRequest request, IssuerState state) =>
            {
                var form = await request.ReadFormAsync();
                var client = AuthenticateClient(options, request, form);
                if (client is null)
                    return TokenError("invalid_client", HttpStatusCode.Unauthorized);
                var q = form.Where(kv => kv.Key != "client_secret").ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
                q["client_id"] = client.ClientId;
                if (ValidateAuthorizeRequest(options, q) is { } error)
                    return TokenError("invalid_request");
                var requestUri = "urn:ietf:params:oauth:request_uri:" + Random32();
                state.PushedRequests[requestUri] = q;
                return Results.Json(new { request_uri = requestUri, expires_in = 60 }, statusCode: 201);
            });
        }

        app.MapGet("/jwks", (IssuerState state) =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(state.Rsa.ExportParameters(false)) { KeyId = state.KeyId });
            return Results.Json(new { keys = new[] { new { kty = jwk.Kty, use = "sig", alg = "RS256", kid = state.KeyId, n = jwk.N, e = jwk.E } } });
        });

        app.MapGet("/authorize", (HttpRequest request, IssuerState state) =>
        {
            var q = Pushed(request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), state);
            if (ValidateAuthorizeRequest(options, q) is { } error)
                return Results.Content(Page("Bad request", $"<p>{Enc(error)}</p>"), "text/html", statusCode: 400);
            return Results.Content(SignInForm(q, state.RecentIdentities()), "text/html");
        });

        app.MapPost("/authorize", async (HttpRequest request, IssuerState state) =>
        {
            var form = await request.ReadFormAsync();
            var q = Pushed(form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), state);
            if (ValidateAuthorizeRequest(options, q) is { } error)
                return Results.Content(Page("Bad request", $"<p>{Enc(error)}</p>"), "text/html", statusCode: 400);

            var redirect = new Dictionary<string, string?> { ["iss"] = issuer };
            if (q.TryGetValue("state", out var st)) redirect["state"] = st;

            if (q.GetValueOrDefault("action") == "cancel")
            {
                redirect["error"] = "access_denied";
                return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(q["redirect_uri"], redirect));
            }

            var sub = q.GetValueOrDefault("sub")?.Trim();
            if (string.IsNullOrEmpty(sub))
                return Results.Content(Page("Bad request", "<p>sub is required.</p>"), "text/html", statusCode: 400);
            var identity = new MockIdentity(
                sub,
                Blank(q.GetValueOrDefault("name")),
                Blank(q.GetValueOrDefault("email")),
                q.GetValueOrDefault("email_verified") switch { "true" => true, "false" => false, _ => null },
                Blank(q.GetValueOrDefault("picture")));
            state.Remember(identity);

            var code = Random32();
            state.Codes[code] = new CodeGrant(q["client_id"], q["redirect_uri"], q["code_challenge"],
                q.GetValueOrDefault("nonce"), identity, DateTimeOffset.UtcNow.AddMinutes(2));
            redirect["code"] = code;
            return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(q["redirect_uri"], redirect));
        });

        app.MapPost("/token", async (HttpRequest request, IssuerState state) =>
        {
            var form = await request.ReadFormAsync();
            var client = AuthenticateClient(options, request, form);
            if (client is null)
                return TokenError("invalid_client", HttpStatusCode.Unauthorized);
            if (form["grant_type"] != "authorization_code")
                return TokenError("unsupported_grant_type");
            if (!state.Codes.TryRemove(form["code"].ToString(), out var grant) || grant.Expires < DateTimeOffset.UtcNow)
                return TokenError("invalid_grant");
            if (grant.ClientId != client.ClientId || grant.RedirectUri != form["redirect_uri"])
                return TokenError("invalid_grant");
            var verifier = form["code_verifier"].ToString();
            if (string.IsNullOrEmpty(verifier) || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != grant.CodeChallenge)
                return TokenError("invalid_grant");

            var now = DateTime.UtcNow;
            var claims = IdentityClaims(grant.Identity);
            if (grant.Nonce is not null) claims["nonce"] = grant.Nonce;
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = client.ClientId,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(5),
                Claims = claims,
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(state.Rsa) { KeyId = state.KeyId }, SecurityAlgorithms.RsaSha256),
            });
            var accessToken = Random32();
            state.AccessTokens[accessToken] = grant.Identity;
            return Results.Json(new { access_token = accessToken, token_type = "Bearer", expires_in = 300, id_token = idToken });
        });

        app.MapGet("/userinfo", (HttpRequest request, IssuerState state) =>
        {
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !state.AccessTokens.TryGetValue(header[7..], out var identity))
                return Results.Unauthorized();
            return Results.Json(IdentityClaims(identity));
        });

        return app;
    }

    /// <summary>
    /// A pushed request carries only <c>client_id</c> and <c>request_uri</c>; the rest was sent to
    /// <c>/par</c>. Anything else in <paramref name="q"/> (the sign-in form's fields) is kept. An
    /// unknown reference leaves the parameters incomplete, so validation rejects them.
    /// </summary>
    static Dictionary<string, string> Pushed(Dictionary<string, string> q, IssuerState state)
    {
        if (q.GetValueOrDefault("request_uri") is not { } requestUri)
            return q;
        if (!state.PushedRequests.TryGetValue(requestUri, out var pushed) || pushed["client_id"] != q.GetValueOrDefault("client_id"))
            return new Dictionary<string, string> { ["client_id"] = q.GetValueOrDefault("client_id") ?? "" };
        var merged = new Dictionary<string, string>(q);
        foreach (var (key, value) in pushed)
            merged[key] = value;
        merged.Remove("request_uri");
        return merged;
    }

    static Dictionary<string, object> IdentityClaims(MockIdentity identity)
    {
        var claims = new Dictionary<string, object> { ["sub"] = identity.Sub };
        if (identity.Name is not null) claims["name"] = identity.Name;
        if (identity.Email is not null) claims["email"] = identity.Email;
        if (identity.EmailVerified is { } verified) claims["email_verified"] = verified;
        if (identity.Picture is not null) claims["picture"] = identity.Picture;
        return claims;
    }

    static string? ValidateAuthorizeRequest(MockIssuerOptions options, IReadOnlyDictionary<string, string> q)
    {
        var client = options.Clients.FirstOrDefault(c => c.ClientId == q.GetValueOrDefault("client_id"));
        if (client is null) return "Unknown client_id.";
        if (!client.RedirectUris.Contains(q.GetValueOrDefault("redirect_uri"))) return "redirect_uri is not registered for this client.";
        if (q.GetValueOrDefault("response_type") != "code") return "Only response_type=code is supported.";
        if (q.GetValueOrDefault("response_mode") is { } mode && mode != "query") return "Only response_mode=query is supported.";
        var scopes = (q.GetValueOrDefault("scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid")) return "The openid scope is required.";
        if (string.IsNullOrEmpty(q.GetValueOrDefault("code_challenge")) || q.GetValueOrDefault("code_challenge_method") != "S256")
            return "PKCE with S256 is required.";
        return null;
    }

    static MockClient? AuthenticateClient(MockIssuerOptions options, HttpRequest request, IFormCollection form)
    {
        string? id = form["client_id"], secret = form["client_secret"];
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])).Split(':', 2);
            if (decoded.Length == 2)
            {
                id = Uri.UnescapeDataString(decoded[0]);
                secret = Uri.UnescapeDataString(decoded[1]);
            }
        }
        var client = options.Clients.FirstOrDefault(c => c.ClientId == id);
        return client is not null && client.ClientSecret == secret ? client : null;
    }

    static IResult TokenError(string error, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        Results.Json(new { error }, statusCode: (int)status);

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static string Random32() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    static readonly string[] PassThrough = ["client_id", "redirect_uri", "response_type", "response_mode", "scope", "state", "nonce", "code_challenge", "code_challenge_method"];

    static string SignInForm(IReadOnlyDictionary<string, string> q, IReadOnlyList<MockIdentity> recent)
    {
        string Hidden() => string.Concat(PassThrough.Where(q.ContainsKey).Select(k => $"<input type=\"hidden\" name=\"{k}\" value=\"{Enc(q[k])}\">"));

        var quick = new StringBuilder();
        foreach (var id in recent)
        {
            quick.Append("<form method=\"post\" action=\"/authorize\" class=\"quick\">").Append(Hidden())
                .Append($"<input type=\"hidden\" name=\"sub\" value=\"{Enc(id.Sub)}\">")
                .Append($"<input type=\"hidden\" name=\"name\" value=\"{Enc(id.Name)}\">")
                .Append($"<input type=\"hidden\" name=\"email\" value=\"{Enc(id.Email)}\">")
                .Append($"<input type=\"hidden\" name=\"email_verified\" value=\"{(id.EmailVerified is { } v ? (v ? "true" : "false") : "omit")}\">")
                .Append($"<input type=\"hidden\" name=\"picture\" value=\"{Enc(id.Picture)}\">")
                .Append($"<button type=\"submit\">{Enc(id.Name ?? id.Sub)} &lt;{Enc(id.Email)}&gt; sub={Enc(id.Sub)}</button></form>");
        }

        var body = $"""
            <p>This is the development identity provider. Whatever you enter here is what Pollyanna receives.</p>
            {(recent.Count > 0 ? "<h2>Sign in again as</h2>" + quick : "")}
            <h2>Sign in as someone</h2>
            <form method="post" action="/authorize">
              {Hidden()}
              <p><label>sub (account ID)<br><input name="sub" required value="alice"></label></p>
              <p><label>Name<br><input name="name" value="Alice Example"></label></p>
              <p><label>Email<br><input name="email" type="email" value="alice@example.com"></label></p>
              <p><label>email_verified<br><select name="email_verified">
                <option value="true">true</option><option value="false">false</option><option value="omit">(leave the claim out)</option>
              </select></label></p>
              <p><label>Picture URL (optional)<br><input name="picture" type="url"></label></p>
              <p><button type="submit" name="action" value="sign-in">Sign in</button>
                 <button type="submit" name="action" value="cancel" formnovalidate>Cancel</button></p>
            </form>
            """;
        return Page("Mock identity provider", body);
    }

    const string Css = "body{font-family:system-ui,sans-serif;max-width:32rem;margin:2rem auto;padding:0 1rem}"
        + "input,select{width:100%;padding:.4rem;font-size:1rem}button{padding:.5rem 1rem;font-size:1rem}"
        + ".quick button{width:100%;margin-bottom:.4rem;text-align:left}";

    static string Page(string title, string body) => $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{Enc(title)}</title>
        <style>{Css}</style>
        </head><body><h1>{Enc(title)}</h1>{body}</body></html>
        """;

    sealed record CodeGrant(string ClientId, string RedirectUri, string CodeChallenge, string? Nonce, MockIdentity Identity, DateTimeOffset Expires);

    sealed class IssuerState : IDisposable
    {
        public RSA Rsa { get; } = RSA.Create(2048);
        public string KeyId { get; } = Random32()[..16];
        public ConcurrentDictionary<string, CodeGrant> Codes { get; } = new();
        public ConcurrentDictionary<string, MockIdentity> AccessTokens { get; } = new();
        public ConcurrentDictionary<string, Dictionary<string, string>> PushedRequests { get; } = new();
        readonly List<MockIdentity> recent = [];

        public void Remember(MockIdentity identity)
        {
            lock (recent)
            {
                recent.RemoveAll(i => i.Sub == identity.Sub);
                recent.Insert(0, identity);
                if (recent.Count > 8) recent.RemoveAt(recent.Count - 1);
            }
        }

        public IReadOnlyList<MockIdentity> RecentIdentities()
        {
            lock (recent) return recent.ToList();
        }

        public void Dispose() => Rsa.Dispose();
    }
}
