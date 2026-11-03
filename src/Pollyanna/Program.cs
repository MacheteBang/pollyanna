using Pollyanna.Auth;
using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Infrastructure;
using Pollyanna.Notifications;
using Pollyanna.People;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

if (Commands.Run(args) is { } exitCode)
    return exitCode;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// Paths and request details never go to the log (spec §8); our own access log records
// method, route pattern and status instead.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// Settings are read lazily so a misconfiguration is reported once, at startup, by StartupTasks.
services.AddSingleton(sp => PollyannaSettings.Load(sp.GetRequiredService<IConfiguration>(), builder.Environment.ContentRootPath));
services.AddSingleton(sp => DrawKey.LoadOrCreate(sp.GetRequiredService<PollyannaSettings>().DrawKeyFile));
services.AddSingleton(TimeProvider.System);
services.AddHostedService<StartupTasks>();
// Email notifications (spec §15). The sender is registered after StartupTasks, so it starts once
// the schema is up to date; with no relay configured it does nothing.
services.AddSingleton<IMailSender, SmtpMailSender>();
services.AddSingleton<OutboxSender>();
services.AddHostedService(sp => sp.GetRequiredService<OutboxSender>());
services.AddScoped<Outbox>();
services.AddScoped<EmailSwitch>();

services.AddDbContext<PollyannaDb>((sp, options) => options
    .UseSqlite($"Data Source={sp.GetRequiredService<PollyannaSettings>().DatabasePath}")
    // SQLite rebuilds a table with foreign keys off, outside the migration's transaction. Expected
    // and harmless (spec §8); every other EF warning still logs.
    .ConfigureWarnings(w => w.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning)));

// Data protection keys protect the in-flight OIDC state and antiforgery tokens; keep them
// with the data so a restart doesn't break forms that are already open. They are stored
// unencrypted on purpose, protected by the data directory's permissions (spec §8); saying so
// also keeps the framework from warning about it.
services.AddDataProtection().SetApplicationName("pollyanna");
services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(o =>
{
    o.XmlRepository = new FileSystemXmlRepository(
        new DirectoryInfo(sp.GetRequiredService<PollyannaSettings>().KeyRingDir),
        sp.GetRequiredService<ILoggerFactory>());
    o.XmlEncryptor = new NullXmlEncryptor();
}));

services.AddHttpClient<PictureFetcher>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MaxAutomaticRedirections = 3, UseCookies = false });
services.AddScoped<PersonSignIn>();
services.AddSingleton<EmailVerifiedHint>();
services.AddScoped<SealedHat>();
services.AddScoped<EventManager>();
services.AddSingleton<PostingLimit>();

const string Oidc = "oidc";
services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<PollyannaSessionOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { })
    .AddOpenIdConnect(Oidc, _ => { });
services.AddOptions<OpenIdConnectOptions>(Oidc).Configure<PollyannaSettings>((o, settings) =>
{
    o.Authority = settings.Issuer;
    o.RequireHttpsMetadata = !settings.IssuerIsLoopback;
    o.ClientId = settings.ClientId;
    o.ClientSecret = settings.ClientSecret;
    o.ResponseType = "code";
    o.ResponseMode = "query";
    o.UsePkce = true;
    o.Scope.Clear();
    o.Scope.Add("openid");
    o.Scope.Add("profile");
    o.Scope.Add("email");
    o.CallbackPath = PollyannaSettings.CallbackPath;
    o.SignInScheme = SessionAuthenticationHandler.SchemeName;
    o.MapInboundClaims = false;
    o.GetClaimsFromUserInfoEndpoint = true;
    o.SaveTokens = false;
    o.DisableTelemetry = true;
    // With response_mode=query the callback is a top-level GET, so Lax is enough.
    foreach (var cookie in new[] { o.CorrelationCookie, o.NonceCookie })
    {
        cookie.SameSite = SameSiteMode.Lax;
        cookie.SecurePolicy = CookieSecurePolicy.Always;
    }
    o.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
    o.ClaimActions.MapUniqueJsonKey("picture", "picture");

    o.Events.OnRedirectToIdentityProvider = context =>
    {
        // Behind a TLS-terminating proxy the request looks like plain HTTP; the callback must
        // be the public URL.
        context.ProtocolMessage.RedirectUri = settings.CallbackUrl;
        return Task.CompletedTask;
    };
    o.Events.OnTicketReceived = async context =>
    {
        var signIn = context.HttpContext.RequestServices.GetRequiredService<PersonSignIn>();
        var person = await signIn.SignInAsync(ProviderIdentity.FromClaims(context.Principal!), context.HttpContext.RequestAborted);
        context.Principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(SessionAuthenticationHandler.PersonIdClaim, person.Id.ToString())],
            Oidc));
    };
    o.Events.OnRemoteFailure = context =>
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(SignInFailure.LoggerCategory);
        context.Response.Redirect(SignInFailure.RemoteFailed(logger, context.Failure));
        context.HandleResponse();
        return Task.CompletedTask;
    };
});

services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
services.AddAntiforgery(o =>
{
    o.Cookie.Name = "pollyanna-af";
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // the scheme comes from the public URL, below
    o.Cookie.SameSite = SameSiteMode.Strict;
});
// One-shot notices after a form post ("Added Carol."), shown by the layout.
services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = "pollyanna-notice";
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.Cookie.SameSite = SameSiteMode.Lax;
});
services.AddRazorPages(o =>
{
    o.Conventions.AllowAnonymousToFolder("/Auth");
    o.Conventions.AllowAnonymousToPage("/Error");
});
services.AddRouting(o => o.LowercaseUrls = true);

// A missing or invalid setting can surface while the pipeline is built (data protection reads
// the data directory) or at startup; either way, list the problems and exit.
try
{
    var app = builder.Build();

    app.UseExceptionHandler("/error");
    app.UseStatusCodePagesWithReExecute("/error/{0}");
    app.UsePublicScheme();
    app.UseSecurityHeaders();
    app.UseAccessLog();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();
    app.MapPictures();
    app.MapRazorPages();

    // Check the settings before the host starts: a failure inside a hosted service would have
    // the host log its own stack trace ahead of the list of problems.
    StartupTasks.CheckConfiguration(app.Services);
    app.Run();
}
catch (PollyannaConfigurationException e)
{
    Console.Error.WriteLine(e.Report(builder.Configuration));
    return 1;
}
return 0;

public partial class Program;
