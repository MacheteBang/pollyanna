using MimeKit;
using Pollyanna.Auth;

namespace Pollyanna.Configuration;

/// <summary>
/// Operator settings (spec §2), read from environment variables. Relative paths are resolved
/// against the content root.
/// </summary>
public sealed class PollyannaSettings
{
    public const string PublicUrlKey = "POLLYANNA_PUBLIC_URL";
    // The unnamed provider's settings; listed providers use POLLYANNA_OIDC_<KEY>_* (ProviderRegistry).
    public const string IssuerKey = "POLLYANNA_OIDC_ISSUER";
    public const string ClientIdKey = "POLLYANNA_OIDC_CLIENT_ID";
    public const string ClientSecretKey = "POLLYANNA_OIDC_CLIENT_SECRET";
    public const string ClientSecretFileKey = "POLLYANNA_OIDC_CLIENT_SECRET_FILE";
    public const string LabelKey = "POLLYANNA_OIDC_LABEL";
    public const string AdmissionKey = "POLLYANNA_OIDC_ADMISSION";
    public const string IconFileKey = "POLLYANNA_OIDC_ICON_FILE";
    public const string ProvidersKey = ProviderRegistry.ProvidersKey;
    public const string DrawKeyFileKey = "POLLYANNA_DRAW_KEY_FILE";
    public const string DataDirKey = "POLLYANNA_DATA_DIR";
    public const string SmtpHostKey = "POLLYANNA_SMTP_HOST";
    public const string SmtpPortKey = "POLLYANNA_SMTP_PORT";
    public const string SmtpTlsKey = "POLLYANNA_SMTP_TLS";
    public const string SmtpUsernameKey = "POLLYANNA_SMTP_USERNAME";
    public const string SmtpPasswordKey = "POLLYANNA_SMTP_PASSWORD";
    public const string SmtpPasswordFileKey = "POLLYANNA_SMTP_PASSWORD_FILE";
    public const string MailFromKey = "POLLYANNA_MAIL_FROM";

    /// <summary>The public base URL, without a trailing slash.</summary>
    public required string PublicUrl { get; init; }
    /// <summary>The sign-in providers, in configured order (spec §2, §3).</summary>
    public required ProviderRegistry Providers { get; init; }
    public required string DrawKeyFile { get; init; }
    public required string DataDir { get; init; }
    /// <summary>The mail relay (spec §15); null when none is configured and email is off.</summary>
    public MailSettings? Mail { get; init; }

    /// <summary>The redirect URI to register at <paramref name="provider"/>.</summary>
    public string RedirectUri(Provider provider) => PublicUrl + provider.CallbackPath;
    public string DatabasePath => Path.Combine(DataDir, "pollyanna.db");
    public string KeyRingDir => Path.Combine(DataDir, "keys");

    public static PollyannaSettings Load(IConfiguration config, string contentRoot)
    {
        var problems = new List<string>();

        string? Required(string key)
        {
            var value = config[key]?.Trim();
            if (string.IsNullOrEmpty(value)) problems.Add($"{key} is not set.");
            return value;
        }

        var publicUrl = Required(PublicUrlKey);
        if (publicUrl is not null)
        {
            publicUrl = ParsePublicUrl(publicUrl, out var problem);
            if (problem is not null) problems.Add(problem);
        }

        var providers = ProviderRegistry.Load(config, contentRoot, problems);

        var drawKeyFile = Required(DrawKeyFileKey);
        var dataDir = Required(DataDirKey);
        var mail = LoadMail(config, contentRoot, problems);

        if (problems.Count > 0)
            throw new PollyannaConfigurationException(problems);

        return new PollyannaSettings
        {
            PublicUrl = publicUrl!,
            Providers = providers!,
            DrawKeyFile = Path.GetFullPath(drawKeyFile!, contentRoot),
            DataDir = Path.GetFullPath(dataDir!, contentRoot),
            Mail = mail,
        };
    }

    /// <summary>
    /// A secret given directly or as a file (at most one of the two); null when neither is set.
    /// Problems name the settings, never the value.
    /// </summary>
    internal static string? Secret(IConfiguration config, string key, string fileKey, string contentRoot, List<string> problems)
    {
        var secret = config[key];
        var file = config[fileKey]?.Trim();
        if (!string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(file))
        {
            problems.Add($"Set only one of {key} and {fileKey}.");
            return null;
        }
        if (string.IsNullOrEmpty(file))
            return string.IsNullOrEmpty(secret) ? null : secret.Trim();
        try
        {
            secret = File.ReadAllText(Path.GetFullPath(file, contentRoot)).Trim();
            if (secret.Length == 0) problems.Add($"The file named by {fileKey} is empty.");
            return secret;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The file named by {fileKey} can't be read ({e.GetType().Name}).");
            return null;
        }
    }

    static readonly string[] MailKeys =
        [SmtpPortKey, SmtpTlsKey, SmtpUsernameKey, SmtpPasswordKey, SmtpPasswordFileKey, MailFromKey];

    /// <summary>
    /// The mail relay (spec §11, §15). With no host, email is off and the rest must be unset too,
    /// so a half-removed configuration doesn't pass silently. With a host, port, TLS mode and from
    /// address are required; a username needs a password and a password needs a username.
    /// </summary>
    static MailSettings? LoadMail(IConfiguration config, string contentRoot, List<string> problems)
    {
        string? Value(string key) => config[key]?.Trim() is { Length: > 0 } v ? v : null;

        var host = Value(SmtpHostKey);
        if (host is null)
        {
            foreach (var key in MailKeys.Where(k => Value(k) is not null))
                problems.Add($"{key} is set but {SmtpHostKey} isn't. Set the relay host, or unset {key} to leave email off.");
            return null;
        }

        var before = problems.Count;
        int port = 0;
        if (Value(SmtpPortKey) is not { } portText)
            problems.Add($"{SmtpPortKey} is not set (needed with {SmtpHostKey}).");
        else if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            problems.Add($"{SmtpPortKey} must be a port number from 1 to 65535.");

        var tls = SmtpTls.None;
        if (Value(SmtpTlsKey) is not { } tlsText)
            problems.Add($"{SmtpTlsKey} is not set (needed with {SmtpHostKey}): use starttls, tls or none.");
        else if (tlsText.ToLowerInvariant() switch
                 {
                     "starttls" => SmtpTls.StartTls,
                     "tls" => SmtpTls.Implicit,
                     "none" => SmtpTls.None,
                     _ => (SmtpTls?)null,
                 } is { } parsed)
            tls = parsed;
        else
            problems.Add($"{SmtpTlsKey} must be starttls, tls (implicit TLS) or none.");

        var username = Value(SmtpUsernameKey);
        var beforePassword = problems.Count;
        var password = Secret(config, SmtpPasswordKey, SmtpPasswordFileKey, contentRoot, problems);
        if (username is not null && password is null && problems.Count == beforePassword)
            problems.Add($"{SmtpUsernameKey} needs {SmtpPasswordKey} or {SmtpPasswordFileKey} with it.");
        if (username is null && password is not null)
            problems.Add($"{SmtpPasswordKey} (or {SmtpPasswordFileKey}) needs {SmtpUsernameKey} with it.");

        MailboxAddress? from = null;
        if (Value(MailFromKey) is not { } fromText)
            problems.Add($"{MailFromKey} is not set (needed with {SmtpHostKey}), e.g. Pollyanna <pollyanna@home.example>.");
        else if (!MailboxAddress.TryParse(fromText, out from) || !from.Address.Contains('@'))
            problems.Add($"{MailFromKey} must be one email address, e.g. Pollyanna <pollyanna@home.example>.");

        return problems.Count > before ? null : new MailSettings(host, port, tls, username, password, from!);
    }

    /// <summary>
    /// The redirect URI of every provider the settings name, if <see cref="PublicUrlKey"/> is valid
    /// on its own, so a settings error can list them and the clients can be registered before the
    /// first start (spec §2, §3).
    /// </summary>
    public static IReadOnlyList<string> RedirectUrisFrom(IConfiguration config) =>
        config[PublicUrlKey]?.Trim() is { Length: > 0 } value && ParsePublicUrl(value, out var problem) is { } url && problem is null
            ? [.. ProviderRegistry.KnownCallbackPaths(config).Select(path => url + path)]
            : [];

    static string? ParsePublicUrl(string value, out string? problem)
    {
        problem = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            problem = $"{PublicUrlKey} must be an absolute http or https URL.";
        else if (u.AbsolutePath != "/" || u.Query != "" || u.Fragment != "")
            problem = $"{PublicUrlKey} must be a bare origin like https://pollyanna.example (no path, query or fragment).";
        else
            return u.GetLeftPart(UriPartial.Authority);
        return null;
    }

    // Keep the client secret out of logs and exception messages.
    public override string ToString() => $"PollyannaSettings {{ PublicUrl = {PublicUrl}, Providers = {string.Join(", ", Providers.Select(p => p.Key))} }}";
}

public enum SmtpTls
{
    /// <summary><c>starttls</c>: upgrade a plain connection, and refuse to go on without it.</summary>
    StartTls,
    /// <summary><c>tls</c>: TLS from the first byte (usually port 465).</summary>
    Implicit,
    /// <summary><c>none</c>: plain SMTP, e.g. a relay on the same host or a development mail catcher.</summary>
    None,
}

/// <summary>How to reach the mail relay (spec §11, §15).</summary>
public sealed record MailSettings(string Host, int Port, SmtpTls Tls, string? Username, string? Password, MailboxAddress From)
{
    // Keep the relay password out of logs and exception messages.
    public override string ToString() => $"MailSettings {{ Host = {Host}, Port = {Port}, Tls = {Tls}, From = {From} }}";
}

public sealed class PollyannaConfigurationException(IReadOnlyList<string> problems)
    : Exception("Pollyanna is not configured correctly:" + string.Concat(problems.Select(p => Environment.NewLine + "  - " + p)))
{
    public IReadOnlyList<string> Problems { get; } = problems;

    /// <summary>
    /// What the operator sees: the problems, every known provider's OIDC redirect URI if the
    /// public URL is valid, then where to read about every setting.
    /// </summary>
    public string Report(IConfiguration config)
    {
        var report = Message;
        switch (PollyannaSettings.RedirectUrisFrom(config))
        {
            case [var one]:
                report += Environment.NewLine + "The OIDC redirect URI to register with the provider is " + one + ".";
                break;
            case { Count: > 1 } several:
                report += Environment.NewLine + "The OIDC redirect URIs to register, one per provider:"
                    + string.Concat(several.Select(uri => Environment.NewLine + "  - " + uri));
                break;
        }
        return report + Environment.NewLine + "Run `/app/Pollyanna help` for every setting.";
    }
}
