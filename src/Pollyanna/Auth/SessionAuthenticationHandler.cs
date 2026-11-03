using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Pollyanna.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Pollyanna.Auth;

public sealed class PollyannaSessionOptions : AuthenticationSchemeOptions
{
    public string CookieName { get; set; } = "__Host-pollyanna";
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(30);
    /// <summary>How stale the expiry may get before a request slides it forward again.</summary>
    public TimeSpan RefreshAfter { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Server-side sessions (spec §3): the cookie holds a random 256-bit token; the database holds
/// its hash, the person and a sliding 30-day expiry. Challenges send the browser to
/// <c>/auth/login</c>, which starts the OIDC flow.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<PollyannaSessionOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PollyannaDb db)
    : SignInAuthenticationHandler<PollyannaSessionOptions>(options, logger, encoder)
{
    public const string SchemeName = "PollyannaSession";
    public const string PersonIdClaim = "pollyanna:person";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[Options.CookieName];
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var id = HashToken(token);
        var found = await db.Sessions
            .Where(s => s.Id == id && s.ExpiresAt > now)
            .Join(db.People, s => s.PersonId, p => p.Id, (s, p) => new { Session = s, p.DisplayName, p.OidcSub })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (found is null || found.OidcSub is null)
            return AuthenticateResult.Fail("No valid session.");

        var session = found.Session;
        if (now + Options.Lifetime - session.ExpiresAt >= Options.RefreshAfter)
        {
            session.ExpiresAt = now + Options.Lifetime;
            await db.SaveChangesAsync(Context.RequestAborted);
            AppendCookie(token, session.ExpiresAt);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(PersonIdClaim, session.PersonId.ToString()),
                new Claim(ClaimTypes.Name, found.DisplayName ?? ""),
            ],
            SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Only a GET can be replayed after sign-in; anything else returns home.
        var returnUrl = HttpMethods.IsGet(Request.Method) || HttpMethods.IsHead(Request.Method)
            ? Request.PathBase + Request.Path + Request.QueryString
            : "/";
        Response.Redirect(QueryHelpers.AddQueryString("/auth/login", "returnUrl", returnUrl));
        return Task.CompletedTask;
    }

    protected override async Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
    {
        var personId = Guid.Parse(user.FindFirstValue(PersonIdClaim)
            ?? throw new InvalidOperationException("Sign-in principal has no person ID."));
        var now = TimeProvider.GetUtcNow().UtcDateTime;

        // Housekeeping: expired sessions are useless; drop them while we're here.
        await db.Sessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync();

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var session = new Session { Id = HashToken(token), PersonId = personId, CreatedAt = now, ExpiresAt = now + Options.Lifetime };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        AppendCookie(token, session.ExpiresAt);
    }

    protected override async Task HandleSignOutAsync(AuthenticationProperties? properties)
    {
        var token = Request.Cookies[Options.CookieName];
        if (!string.IsNullOrEmpty(token))
        {
            var id = HashToken(token);
            await db.Sessions.Where(s => s.Id == id).ExecuteDeleteAsync();
        }
        Response.Cookies.Delete(Options.CookieName, CookieOptions(null));
    }

    void AppendCookie(string token, DateTime expires) =>
        Response.Cookies.Append(Options.CookieName, token, CookieOptions(expires));

    static CookieOptions CookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        Expires = expires,
    };

    internal static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in person's Pollyanna ID.</summary>
    public static Guid PersonId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(SessionAuthenticationHandler.PersonIdClaim)
            ?? throw new InvalidOperationException("Not signed in."));
}
