using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Pollyanna.Auth;

/// <summary>
/// Carries an open provider's refusal to <c>/auth/failed</c> (spec §3). The reason, the address
/// just used and the provider travel as short-lived protected data, so a crafted URL can't make
/// the page show an address or a reason of its choosing.
/// </summary>
public sealed class RefusedSignIn(IDataProtectionProvider protection)
{
    /// <summary>The query parameter that holds the protected refusal.</summary>
    public const string QueryKey = "refused";
    /// <summary>Long enough to land on the page and reload it, short enough not to linger.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    readonly ITimeLimitedDataProtector protector =
        protection.CreateProtector("Pollyanna.Auth.RefusedSignIn").ToTimeLimitedDataProtector();

    /// <summary>What the refused page shows: the provider's key, the reason, the address, and where the person was going.</summary>
    public sealed record Refusal(string Provider, RefusalReason Reason, string? Address, string? ReturnUrl);

    /// <summary>Where to send someone whose sign-in through <paramref name="provider"/> was refused.</summary>
    public string PagePath(Provider provider, SignInRefusal refusal, string? returnUrl)
    {
        var json = JsonSerializer.Serialize(new Refusal(provider.Key, refusal.Reason, refusal.Address, returnUrl));
        return SignInFailure.PagePath + "?" + QueryKey + "=" + Uri.EscapeDataString(protector.Protect(json, Lifetime));
    }

    /// <summary>The refusal a page link carries; null when it is missing, tampered with or expired.</summary>
    public Refusal? Read(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Refusal>(protector.Unprotect(token, out _));
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }
}
