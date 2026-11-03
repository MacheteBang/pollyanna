using Pollyanna.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pollyanna.Tests;

/// <summary>Operator settings and the draw key (spec §2, §5.3, §11).</summary>
public sealed class ConfigurationTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pollyanna-config-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    static Dictionary<string, string?> Valid() => new()
    {
        [PollyannaSettings.PublicUrlKey] = "https://pollyanna.example",
        [PollyannaSettings.IssuerKey] = "https://id.example/",
        [PollyannaSettings.ClientIdKey] = "pollyanna",
        [PollyannaSettings.ClientSecretKey] = "s3cret",
        [PollyannaSettings.DrawKeyFileKey] = "/run/secrets/draw.key",
        [PollyannaSettings.DataDirKey] = "data",
    };

    PollyannaSettings Load(Dictionary<string, string?> values) =>
        PollyannaSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), dir);

    [Fact]
    public void Loads_valid_settings_and_resolves_relative_paths()
    {
        var s = Load(Valid());
        Assert.Equal("https://pollyanna.example", s.PublicUrl);
        Assert.Equal("https://pollyanna.example/auth/callback", s.CallbackUrl);
        Assert.Equal("https://id.example", s.Issuer);
        Assert.Equal("s3cret", s.ClientSecret);
        Assert.Equal(Path.Combine(dir, "data"), s.DataDir);
        Assert.Equal("/run/secrets/draw.key", s.DrawKeyFile);
        Assert.DoesNotContain("s3cret", s.ToString());
    }

    [Fact]
    public void Lists_every_missing_setting_at_once()
    {
        var e = Assert.Throws<PollyannaConfigurationException>(() => Load([]));
        Assert.Equal(6, e.Problems.Count);
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.PublicUrlKey));
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.ClientSecretFileKey));
    }

    [Fact]
    public void Reads_the_client_secret_from_a_file()
    {
        var file = Path.Combine(dir, "secret");
        File.WriteAllText(file, "from-file\n");
        var values = Valid();
        values.Remove(PollyannaSettings.ClientSecretKey);
        values[PollyannaSettings.ClientSecretFileKey] = file;
        Assert.Equal("from-file", Load(values).ClientSecret);
    }

    [Fact]
    public void Refuses_both_secret_and_secret_file()
    {
        var values = Valid();
        values[PollyannaSettings.ClientSecretFileKey] = "x";
        Assert.Throws<PollyannaConfigurationException>(() => Load(values));
    }

    [Fact]
    public void Missing_secret_file_is_reported_without_its_contents()
    {
        var values = Valid();
        values.Remove(PollyannaSettings.ClientSecretKey);
        values[PollyannaSettings.ClientSecretFileKey] = Path.Combine(dir, "nope");
        var e = Assert.Throws<PollyannaConfigurationException>(() => Load(values));
        Assert.Contains(e.Problems, p => p.Contains("can't be read"));
    }

    [Theory]
    [InlineData("http://id.example")]
    [InlineData("not a url")]
    public void Issuer_must_be_https_unless_on_localhost(string issuer)
    {
        var values = Valid();
        values[PollyannaSettings.IssuerKey] = issuer;
        Assert.Throws<PollyannaConfigurationException>(() => Load(values));
    }

    [Theory]
    [InlineData("http://localhost:5021")]
    [InlineData("http://127.0.0.1:5021")]
    public void Plain_http_issuer_is_allowed_on_localhost(string issuer)
    {
        var values = Valid();
        values[PollyannaSettings.IssuerKey] = issuer;
        Assert.True(Load(values).IssuerIsLoopback);
    }

    [Theory]
    [InlineData("https://example.com/pollyanna")]
    [InlineData("https://example.com/?x=1")]
    [InlineData("pollyanna.example")]
    public void Public_url_must_be_a_bare_origin(string url)
    {
        var values = Valid();
        values[PollyannaSettings.PublicUrlKey] = url;
        Assert.Throws<PollyannaConfigurationException>(() => Load(values));
    }

    static Dictionary<string, string?> WithMail(params (string Key, string? Value)[] mail)
    {
        var values = Valid();
        foreach (var (key, value) in mail)
            values[key] = value;
        return values;
    }

    static readonly (string, string?)[] Relay =
    [
        (PollyannaSettings.SmtpHostKey, "mail.example"),
        (PollyannaSettings.SmtpPortKey, "587"),
        (PollyannaSettings.SmtpTlsKey, "starttls"),
        (PollyannaSettings.MailFromKey, "Pollyanna <pollyanna@home.example>"),
    ];

    [Fact]
    public void Without_a_mail_relay_email_is_off()
    {
        Assert.Null(Load(Valid()).Mail);
    }

    [Fact]
    public void Loads_the_mail_relay()
    {
        var mail = Load(WithMail([.. Relay, (PollyannaSettings.SmtpUsernameKey, "relay-user"), (PollyannaSettings.SmtpPasswordKey, "relay-pass")])).Mail;
        Assert.NotNull(mail);
        Assert.Equal("mail.example", mail.Host);
        Assert.Equal(587, mail.Port);
        Assert.Equal(SmtpTls.StartTls, mail.Tls);
        Assert.Equal("relay-user", mail.Username);
        Assert.Equal("relay-pass", mail.Password);
        Assert.Equal("Pollyanna", mail.From.Name);
        Assert.Equal("pollyanna@home.example", mail.From.Address);
        Assert.DoesNotContain("relay-pass", mail.ToString());
    }

    [Fact]
    public void Reads_the_relay_password_from_a_file()
    {
        var file = Path.Combine(dir, "relay");
        File.WriteAllText(file, "from-file\n");
        var mail = Load(WithMail([.. Relay, (PollyannaSettings.SmtpUsernameKey, "u"), (PollyannaSettings.SmtpPasswordFileKey, file)])).Mail;
        Assert.Equal("from-file", mail!.Password);
    }

    [Theory]
    [InlineData(PollyannaSettings.SmtpPortKey)]
    [InlineData(PollyannaSettings.SmtpTlsKey)]
    [InlineData(PollyannaSettings.SmtpUsernameKey)]
    [InlineData(PollyannaSettings.SmtpPasswordKey)]
    [InlineData(PollyannaSettings.MailFromKey)]
    public void Mail_settings_without_a_relay_host_stop_startup(string key)
    {
        var e = Assert.Throws<PollyannaConfigurationException>(() => Load(WithMail((key, "x"))));
        Assert.Contains(e.Problems, p => p.Contains(key) && p.Contains(PollyannaSettings.SmtpHostKey));
    }

    [Fact]
    public void An_incomplete_relay_is_listed_with_the_other_problems()
    {
        var values = WithMail((PollyannaSettings.SmtpHostKey, "mail.example"), (PollyannaSettings.SmtpUsernameKey, "u"));
        values.Remove(PollyannaSettings.DataDirKey);
        var e = Assert.Throws<PollyannaConfigurationException>(() => Load(values));
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.DataDirKey));
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.SmtpPortKey));
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.SmtpTlsKey));
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.MailFromKey));
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.SmtpPasswordKey));
    }

    [Theory]
    [InlineData(PollyannaSettings.SmtpPortKey, "0")]
    [InlineData(PollyannaSettings.SmtpPortKey, "65536")]
    [InlineData(PollyannaSettings.SmtpPortKey, "smtp")]
    [InlineData(PollyannaSettings.SmtpTlsKey, "ssl")]
    [InlineData(PollyannaSettings.MailFromKey, "not an address")]
    [InlineData(PollyannaSettings.SmtpPasswordKey, "a password but no username")]
    public void Invalid_mail_settings_stop_startup(string key, string value)
    {
        var e = Assert.Throws<PollyannaConfigurationException>(() => Load(WithMail([.. Relay, (key, value)])));
        Assert.Contains(e.Problems, p => p.Contains(key));
        Assert.DoesNotContain(e.Problems, p => p.Contains("a password but no username"));
    }

    [Fact]
    public void Refuses_both_relay_password_and_password_file()
    {
        var e = Assert.Throws<PollyannaConfigurationException>(() => Load(WithMail(
            [.. Relay, (PollyannaSettings.SmtpUsernameKey, "u"), (PollyannaSettings.SmtpPasswordKey, "p"), (PollyannaSettings.SmtpPasswordFileKey, "f")])));
        Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.SmtpPasswordFileKey));
    }

    [Theory]
    [InlineData("tls", SmtpTls.Implicit)]
    [InlineData("NONE", SmtpTls.None)]
    public void Tls_modes(string value, SmtpTls expected)
    {
        Assert.Equal(expected, Load(WithMail([.. Relay, (PollyannaSettings.SmtpTlsKey, value)])).Mail!.Tls);
    }

    [Fact]
    public void Draw_key_is_created_once_with_32_random_bytes_readable_only_by_the_owner()
    {
        var path = Path.Combine(dir, "secrets", "draw.key");
        var key = DrawKey.LoadOrCreate(path);

        Assert.Equal(32, new FileInfo(path).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("[draw key]", key.ToString());

        var again = DrawKey.LoadOrCreate(path);
        Assert.True(key.Bytes.SequenceEqual(again.Bytes));

        var other = DrawKey.LoadOrCreate(Path.Combine(dir, "other.key"));
        Assert.False(key.Bytes.SequenceEqual(other.Bytes));
    }

    [Fact]
    public void Draw_key_of_the_wrong_size_stops_startup()
    {
        var path = Path.Combine(dir, "bad.key");
        File.WriteAllBytes(path, new byte[16]);
        var e = Assert.Throws<PollyannaConfigurationException>(() => DrawKey.LoadOrCreate(path));
        var problem = Assert.Single(e.Problems);
        Assert.Contains("32 raw bytes (not base64 or hex)", problem);
        Assert.Contains("If no draw has been made yet, delete it", problem);
    }

    [Fact]
    public void Draw_key_that_cant_be_created_stops_startup_with_its_path()
    {
        var blocker = Path.Combine(dir, "not-a-dir");
        File.WriteAllText(blocker, "");
        var path = Path.Combine(blocker, "draw.key");
        var e = Assert.Throws<PollyannaConfigurationException>(() => DrawKey.LoadOrCreate(path));
        Assert.Contains(path, Assert.Single(e.Problems));
    }
}
