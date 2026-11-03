using System.Collections.Concurrent;
using System.Net;
using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Notifications;
using Pollyanna.People;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Pollyanna.Tests.Support;

/// <summary>
/// Pollyanna wired to an in-process mock issuer, a fresh data directory, a fake clock and a stub
/// for picture downloads. One per test.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    public const string PublicUrl = "https://pollyanna.test";
    public const string Issuer = "https://issuer.test";
    public const string ClientId = "pollyanna-test";
    public const string ClientSecret = "test-secret";

    readonly WebApplication issuer;
    readonly WebApplicationFactory<Program> factory;

    public string DataDir { get; }
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public StubHttpHandler Pictures { get; } = new();
    public LogCapture Logs { get; } = new();
    /// <summary>What the outbox would have sent; only used when started with a mail relay.</summary>
    public FakeMailSender Mail { get; } = new();

    public const string MailHost = "mail.test";
    public const string MailFrom = "Pollyanna <pollyanna@pollyanna.test>";

    TestApp(WebApplication issuer, string dataDir, bool mail, Action<IWebHostBuilder>? configure)
    {
        this.issuer = issuer;
        DataDir = dataDir;
        var issuerHandler = issuer.GetTestServer().CreateHandler();

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting(PollyannaSettings.PublicUrlKey, PublicUrl);
            b.UseSetting(PollyannaSettings.IssuerKey, Issuer);
            b.UseSetting(PollyannaSettings.ClientIdKey, ClientId);
            b.UseSetting(PollyannaSettings.ClientSecretKey, ClientSecret);
            b.UseSetting(PollyannaSettings.DrawKeyFileKey, Path.Combine(dataDir, "secrets", "draw.key"));
            b.UseSetting(PollyannaSettings.DataDirKey, Path.Combine(dataDir, "data"));
            if (mail)
            {
                b.UseSetting(PollyannaSettings.SmtpHostKey, MailHost);
                b.UseSetting(PollyannaSettings.SmtpPortKey, "25");
                b.UseSetting(PollyannaSettings.SmtpTlsKey, "none");
                b.UseSetting(PollyannaSettings.MailFromKey, MailFrom);
            }
            b.ConfigureLogging(l => l.AddProvider(Logs));
            b.ConfigureTestServices(services =>
            {
                services.Configure<OpenIdConnectOptions>("oidc", o => o.BackchannelHttpHandler = issuerHandler);
                services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient<PictureFetcher>().ConfigurePrimaryHttpMessageHandler(() => Pictures);
                services.AddSingleton<IMailSender>(Mail);
                // Mail goes out only when a test calls SendMailAsync, never on the sender's own.
                services.Configure<OutboxSenderOptions>(o => o.SendInBackground = false);
            });
            configure?.Invoke(b);
        });
    }

    /// <param name="pushedAuthorization">Whether the mock issuer offers PAR, which Pollyanna then uses.</param>
    /// <param name="configure">Runs after the defaults, e.g. to change a setting or the backchannel.</param>
    /// <param name="mail">Configures a mail relay (spec §15), with <see cref="Mail"/> standing in for it.</param>
    public static async Task<TestApp> StartAsync(bool pushedAuthorization = false, Action<IWebHostBuilder>? configure = null, bool mail = false)
    {
        var dir = Directory.CreateTempSubdirectory("pollyanna-test-").FullName;
        var issuer = MockIssuerApp.Build(
            new MockIssuerOptions
            {
                Issuer = Issuer,
                Clients = [new MockClient { ClientId = ClientId, ClientSecret = ClientSecret, RedirectUris = [PublicUrl + "/auth/callback"] }],
                PushedAuthorization = pushedAuthorization,
            },
            b =>
            {
                b.WebHost.UseTestServer();
                b.Logging.ClearProviders();
            });
        await issuer.StartAsync();
        var app = new TestApp(issuer, dir, mail, configure);
        _ = app.factory.Server; // start Pollyanna (runs migrations)
        return app;
    }

    public IServiceProvider Services => factory.Services;

    /// <summary>
    /// Runs the outbox now, as the background sender would: sends or drops every row that is due
    /// at <see cref="Clock"/>'s time. The background sender doesn't run in tests, so this is the
    /// only way mail goes out.
    /// </summary>
    public Task SendMailAsync() => Services.GetRequiredService<OutboxSender>().SendDueAsync(CancellationToken.None);

    public TestBrowser NewBrowser() => new(
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(PublicUrl),
            HandleCookies = true,
        }),
        new HttpClient(issuer.GetTestServer().CreateHandler()) { BaseAddress = new Uri(Issuer) });

    /// <summary>A bare client with no cookie handling, e.g. to talk plain HTTP like the proxy does.</summary>
    public HttpClient CreateRawClient(string baseAddress) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri(baseAddress),
        HandleCookies = false,
    });

    public async Task<T> WithDbAsync<T>(Func<PollyannaDb, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PollyannaDb>());
    }

    public Task WithDbAsync(Func<PollyannaDb, Task> action) =>
        WithDbAsync(async db => { await action(db); return true; });

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        await issuer.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Answers HTTP requests from a table, for picture downloads.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> responses = new();
    public ConcurrentBag<string> Requested { get; } = [];
    public TimeSpan Delay { get; set; }

    public void Serve(string url, byte[] body, string contentType = "application/octet-stream", HttpStatusCode status = HttpStatusCode.OK) =>
        responses[url] = () => new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", contentType } } },
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested.Add(request.RequestUri!.ToString());
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
        return responses.TryGetValue(request.RequestUri!.ToString(), out var make)
            ? make()
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>Keeps every log message, for checks that nothing sensitive is logged.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, string Message, LogLevel Level)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, formatter(state, exception) + (exception is null ? "" : " " + exception), logLevel));
    }
}

public static class TestImages
{
    public static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 1, 2, 3];
    public static readonly byte[] Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray();
}
