using System.Collections;
using System.Net;
using System.Text.RegularExpressions;
using Pollyanna.Configuration;

namespace Pollyanna.Auth;

/// <summary>Who a provider lets in (spec §3).</summary>
public enum Admission
{
    /// <summary>Anyone it authenticates gets in.</summary>
    Trusted,
    /// <summary>Only someone whose verified email matches a person or a pending invitation.</summary>
    Open,
}

/// <summary>
/// One configured OpenID Connect provider: the unnamed <c>POLLYANNA_OIDC_*</c> set (key
/// <c>default</c>) or one listed in <c>POLLYANNA_OIDC_PROVIDERS</c>. Each has its own
/// authentication scheme and callback path.
/// </summary>
public sealed class Provider
{
    public const string DefaultKey = "default";
    public const string DefaultScheme = "oidc";
    public const string DefaultCallbackPath = "/auth/callback";

    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>The issuer URL, without a trailing slash.</summary>
    public required string Issuer { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required Admission Admission { get; init; }
    /// <summary>The logo beside the label on this provider's buttons; null for a text-only button.</summary>
    public ProviderIcon? Icon { get; init; }

    public bool IsDefault => Key == DefaultKey;
    /// <summary>The OpenID Connect authentication scheme that signs in through this provider.</summary>
    public string Scheme => IsDefault ? DefaultScheme : DefaultScheme + "-" + Key;
    /// <summary>Where the provider sends people back: <c>/auth/callback</c>, or <c>/auth/callback/&lt;key&gt;</c>.</summary>
    public string CallbackPath => IsDefault ? DefaultCallbackPath : DefaultCallbackPath + "/" + Key;
    /// <summary>Starts sign-in through this provider (the chooser's buttons).</summary>
    public string LoginPath => "/auth/login/" + Key;
    /// <summary>
    /// Where the icon is served (<see cref="ProviderIconEndpoint"/>), with a version so a changed
    /// file reaches browsers that cached the old one; null without an icon.
    /// </summary>
    public string? IconPath => Icon is null ? null : ProviderIconEndpoint.PathPrefix + Key + "?v=" + Icon.Version;
    /// <summary>The full name of one of this provider's settings, e.g. <c>POLLYANNA_OIDC_GOOGLE_ISSUER</c>.</summary>
    public string Setting(string field) => SettingName(IsDefault ? null : Key, field);
    /// <summary>True when the issuer may be reached over plain HTTP (loopback only, for development).</summary>
    public bool IssuerIsLoopback => Uri.TryCreate(Issuer, UriKind.Absolute, out var u) && ProviderRegistry.IsLoopback(u);

    internal static string SettingName(string? key, string field) =>
        ProviderRegistry.Prefix + (key is null ? "" : key.ToUpperInvariant() + "_") + field;

    // Keep the client secret out of logs and exception messages.
    public override string ToString() => $"Provider {{ Key = {Key}, Label = {Label}, Issuer = {Issuer}, Admission = {Admission} }}";
}

/// <summary>
/// The configured sign-in providers, in order: the unnamed provider first (if configured), then
/// <c>POLLYANNA_OIDC_PROVIDERS</c> in list order (spec §2, §3). Everything else asks it: the list,
/// a provider by key, issuer or scheme, and whether any provider is open.
/// </summary>
public sealed partial class ProviderRegistry(IReadOnlyList<Provider> providers) : IReadOnlyList<Provider>
{
    internal const string Prefix = "POLLYANNA_OIDC_";
    public const string ProvidersKey = Prefix + "PROVIDERS";
    public const string IssuerField = "ISSUER";
    public const string ClientIdField = "CLIENT_ID";
    public const string ClientSecretField = "CLIENT_SECRET";
    public const string ClientSecretFileField = "CLIENT_SECRET_FILE";
    public const string LabelField = "LABEL";
    public const string AdmissionField = "ADMISSION";
    public const string IconFileField = "ICON_FILE";

    static readonly string[] Fields = [IssuerField, ClientIdField, ClientSecretField, ClientSecretFileField, LabelField, AdmissionField, IconFileField];

    public Provider this[int index] => providers[index];
    public int Count => providers.Count;
    public IEnumerator<Provider> GetEnumerator() => providers.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>With exactly one provider, that one: <c>/auth/login</c> goes straight to it.</summary>
    public Provider? Single => providers.Count == 1 ? providers[0] : null;
    public Provider? ByKey(string? key) => providers.FirstOrDefault(p => p.Key == key);
    /// <summary>The provider a login's issuer belongs to; null for a provider that was removed.</summary>
    public Provider? ByIssuer(string? issuer) => issuer is null ? null : providers.FirstOrDefault(p => p.Issuer == issuer.TrimEnd('/'));
    public Provider? ByScheme(string? scheme) => providers.FirstOrDefault(p => p.Scheme == scheme);
    public bool AnyOpen => providers.Any(p => p.Admission == Admission.Open);
    /// <summary>The open providers' labels, in configured order (for the invitation email).</summary>
    public IReadOnlyList<string> OpenLabels => [.. providers.Where(p => p.Admission == Admission.Open).Select(p => p.Label)];

    /// <summary>
    /// Reads every provider's settings, adding a problem (naming its setting in full) for each
    /// mistake. Returns null when there were problems.
    /// </summary>
    internal static ProviderRegistry? Load(IConfiguration config, string contentRoot, List<string> problems)
    {
        var before = problems.Count;
        var listed = ListedKeys(config, problems, out var allTokens);
        var result = new List<Provider>();

        if (UnnamedIsConfigured(config, allTokens.Count))
            if (LoadOne(config, null, contentRoot, problems) is { } unnamed)
                result.Add(unnamed);
        foreach (var key in listed)
            if (LoadOne(config, key, contentRoot, problems) is { } provider)
                result.Add(provider);

        foreach (var setting in config.AsEnumerable().Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key))
            if (IsUnlistedSetting(setting, allTokens))
                problems.Add($"{setting} is set, but {ProvidersKey} doesn't list its key. Add the key there, or unset {setting}.");

        foreach (var group in result.GroupBy(p => p.Issuer).Where(g => g.Count() > 1))
        {
            var names = group.Select(p => p.Setting(IssuerField)).ToList();
            problems.Add($"{string.Join(" and ", names)} name the same issuer ({group.Key}). Configure each provider once.");
        }

        return problems.Count > before ? null : new ProviderRegistry(result);
    }

    /// <summary>
    /// The callback path of every provider the settings name, valid or not, so a settings error
    /// can list the redirect URIs to register (spec §2).
    /// </summary>
    public static IReadOnlyList<string> KnownCallbackPaths(IConfiguration config)
    {
        var listed = ListedKeys(config, [], out var allTokens);
        var paths = new List<string>();
        if (UnnamedIsConfigured(config, allTokens.Count))
            paths.Add(Provider.DefaultCallbackPath);
        paths.AddRange(listed.Select(k => Provider.DefaultCallbackPath + "/" + k));
        return paths;
    }

    /// <summary>
    /// The valid keys of <c>POLLYANNA_OIDC_PROVIDERS</c>, in order and without repeats.
    /// <paramref name="allTokens"/> holds every token, lowercased, so a misspelled key's settings
    /// aren't reported twice.
    /// </summary>
    static List<string> ListedKeys(IConfiguration config, List<string> problems, out HashSet<string> allTokens)
    {
        allTokens = [];
        var keys = new List<string>();
        var tokens = (config[ProvidersKey] ?? "").Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            allTokens.Add(token.ToLowerInvariant());
            if (!KeyPattern().IsMatch(token))
                problems.Add($"{ProvidersKey} lists \"{token}\", which isn't a valid key: use lowercase letters and digits, starting with a letter.");
            else if (token == Provider.DefaultKey)
                problems.Add($"{ProvidersKey} lists \"{Provider.DefaultKey}\", which is reserved for the unnamed POLLYANNA_OIDC_* provider. Choose another key.");
            else if (keys.Contains(token))
                problems.Add($"{ProvidersKey} lists \"{token}\" more than once.");
            else
                keys.Add(token);
        }
        return keys;
    }

    /// <summary>
    /// The unnamed provider exists when any of its settings is set. With no listed providers it is
    /// required, so at least one provider always exists.
    /// </summary>
    static bool UnnamedIsConfigured(IConfiguration config, int listedTokens) =>
        listedTokens == 0 || Fields.Any(f => !string.IsNullOrWhiteSpace(config[Provider.SettingName(null, f)]));

    static bool IsUnlistedSetting(string setting, HashSet<string> listedTokens)
    {
        if (setting.Contains(':') || !setting.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = setting[Prefix.Length..].ToUpperInvariant();
        if (rest == "PROVIDERS" || Fields.Contains(rest))
            return false;
        foreach (var field in Fields)
            if (rest.EndsWith("_" + field) && listedTokens.Contains(rest[..^(field.Length + 1)].ToLowerInvariant()))
                return false;
        return true;
    }

    static Provider? LoadOne(IConfiguration config, string? key, string contentRoot, List<string> problems)
    {
        var before = problems.Count;
        string Name(string field) => Provider.SettingName(key, field);
        string? Value(string field) => config[Name(field)]?.Trim() is { Length: > 0 } v ? v : null;
        string? Required(string field)
        {
            var value = Value(field);
            if (value is null) problems.Add($"{Name(field)} is not set.");
            return value;
        }

        var issuer = Required(IssuerField);
        Uri? issuerUri = null;
        if (issuer is not null)
        {
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out issuerUri) || (issuerUri.Scheme != "https" && issuerUri.Scheme != "http"))
                problems.Add($"{Name(IssuerField)} must be an absolute http or https URL.");
            else if (issuerUri.Scheme == "http" && !IsLoopback(issuerUri))
                problems.Add($"{Name(IssuerField)} must use https (plain http is allowed only for a provider on localhost).");
            else
                issuer = issuer.TrimEnd('/');
        }

        var clientId = Required(ClientIdField);

        var beforeSecret = problems.Count;
        var secret = PollyannaSettings.Secret(config, Name(ClientSecretField), Name(ClientSecretFileField), contentRoot, problems);
        if (secret is null && problems.Count == beforeSecret)
            problems.Add($"{Name(ClientSecretField)} or {Name(ClientSecretFileField)} must be set.");

        var label = key is null ? Value(LabelField) ?? issuerUri?.Host : Required(LabelField);

        var admission = Admission.Trusted;
        if (Value(AdmissionField) is { } admissionText)
        {
            switch (admissionText.ToLowerInvariant())
            {
                case "trusted":
                    break;
                case "open":
                    // Lifted by the open providers' admission gate; until then main never runs an open provider without it.
                    problems.Add($"{Name(AdmissionField)} is open, but open admission is not supported yet. Use trusted.");
                    break;
                default:
                    problems.Add($"{Name(AdmissionField)} must be trusted or open.");
                    break;
            }
        }
        else if (key is not null)
            problems.Add($"{Name(AdmissionField)} is not set: use trusted or open.");

        ProviderIcon? icon = null;
        if (Value(IconFileField) is { } iconFile)
            icon = ProviderIcon.Load(Name(IconFileField), iconFile, contentRoot, problems);

        if (problems.Count > before)
            return null;
        return new Provider
        {
            Key = key ?? Provider.DefaultKey,
            Label = label!,
            Issuer = issuer!,
            ClientId = clientId!,
            ClientSecret = secret!.Trim(),
            Admission = admission,
            Icon = icon,
        };
    }

    internal static bool IsLoopback(Uri u) =>
        u.IsLoopback || (IPAddress.TryParse(u.Host, out var ip) && IPAddress.IsLoopback(ip));

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex KeyPattern();
}
