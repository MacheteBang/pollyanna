using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Pollyanna.Auth;

/// <summary>
/// Sign-in failures go to <c>/auth/failed</c>, never the general error page (spec §3). The log
/// names the provider's <c>error</c> code and what to check; the page shows no details. Only
/// exception types and provider error codes are logged, never messages that could carry a secret.
/// </summary>
public static partial class SignInFailure
{
    public const string LoggerCategory = "Pollyanna.Auth";
    const string Page = "/auth/failed";
    /// <summary>The page adds that whoever runs the site needs to check its settings.</summary>
    const string ProviderPage = Page + "?reason=provider";
    const string CheckClient = "Check POLLYANNA_OIDC_CLIENT_ID, the client secret and the client registration.";

    /// <summary>
    /// Building the redirect to the provider failed (discovery, metadata, a rejected pushed
    /// authorization request): Pollyanna's settings or the provider's are wrong. Returns where to send the person.
    /// </summary>
    public static string StartFailed(ILogger logger, Exception e, string issuer)
    {
        switch (ProviderError(e))
        {
            case "invalid_client":
                logger.LogError("Sign-in could not start: the provider rejected the client (invalid_client). " + CheckClient);
                break;
            case { } error:
                logger.LogError("Sign-in could not start: the provider answered {Error}. Check the client registration.", error);
                break;
            default:
                logger.LogError(
                    "Sign-in could not start: the provider's configuration couldn't be loaded from {Issuer} ({Exception}). "
                    + "Check POLLYANNA_OIDC_ISSUER and that Pollyanna can reach it.", issuer, Innermost(e).GetType().Name);
                break;
        }
        return ProviderPage;
    }

    /// <summary>The provider sent the person back with an error, or the code exchange failed.</summary>
    public static string RemoteFailed(ILogger logger, Exception? e)
    {
        var error = ProviderError(e);
        if (error is "invalid_client" or "unauthorized_client")
        {
            logger.LogWarning("Sign-in failed: {Error}. " + CheckClient, error);
            return ProviderPage;
        }
        logger.LogWarning("Sign-in failed: {Error}", error ?? (e is null ? "unknown" : Innermost(e).GetType().Name));
        return Chain(e).Any(x => x is HttpRequestException) ? ProviderPage : Page;
    }

    /// <summary>The OAuth <c>error</c> code the provider sent, if the failure carries one.</summary>
    internal static string? ProviderError(Exception? e)
    {
        foreach (var x in Chain(e).OfType<OpenIdConnectProtocolException>())
        {
            if (x.Data["error"] is string { Length: > 0 } error)
                return error;
            if (ErrorInMessage().Match(x.Message) is { Success: true } m)
                return m.Groups[1].Value;
        }
        return null;
    }

    static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            yield return e;
    }

    static Exception Innermost(Exception e) => Chain(e).Last();

    // "Message contains error: 'invalid_client', error_description: ..."
    [GeneratedRegex("error: '([A-Za-z0-9_.-]+)'")]
    private static partial Regex ErrorInMessage();
}
