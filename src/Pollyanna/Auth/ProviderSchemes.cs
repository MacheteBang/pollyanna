using System.Security.Claims;
using Pollyanna.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Pollyanna.Auth;

/// <summary>
/// One OpenID Connect authentication scheme per configured provider (spec §3). The providers come
/// from the settings, which are read lazily so a misconfiguration is reported once at startup;
/// so the schemes are added on first use rather than while the services are registered.
/// </summary>
public static class ProviderSchemes
{
    public static IServiceCollection AddProviderSchemes(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<PollyannaSettings>().Providers);
        services.AddSingleton<IAuthenticationSchemeProvider, SchemeProvider>();
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, ConfigureProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
        return services;
    }

    /// <summary>The registered schemes plus one OpenID Connect scheme per provider, added on first use.</summary>
    sealed class SchemeProvider(IOptions<AuthenticationOptions> options, IServiceProvider services) : AuthenticationSchemeProvider(options)
    {
        volatile bool added;

        void EnsureAdded()
        {
            if (added) return;
            lock (this)
            {
                if (added) return;
                foreach (var provider in services.GetRequiredService<ProviderRegistry>())
                    TryAddScheme(new AuthenticationScheme(provider.Scheme, provider.Label, typeof(OpenIdConnectHandler)));
                added = true;
            }
        }

        public override Task<AuthenticationScheme?> GetSchemeAsync(string name) { EnsureAdded(); return base.GetSchemeAsync(name); }
        public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() { EnsureAdded(); return base.GetAllSchemesAsync(); }
        public override Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() { EnsureAdded(); return base.GetRequestHandlerSchemesAsync(); }
    }

    /// <summary>
    /// The same client for every provider: authorization code with PKCE, <c>openid profile email</c>,
    /// pushed authorization when the provider advertises it, and its own callback path.
    /// </summary>
    sealed class ConfigureProvider(IServiceProvider services) : IConfigureNamedOptions<OpenIdConnectOptions>
    {
        public void Configure(OpenIdConnectOptions options) { }

        public void Configure(string? name, OpenIdConnectOptions o)
        {
            var settings = services.GetRequiredService<PollyannaSettings>();
            if (settings.Providers.ByScheme(name) is not { } provider)
                return;

            o.Authority = provider.Issuer;
            o.RequireHttpsMetadata = !provider.IssuerIsLoopback;
            o.ClientId = provider.ClientId;
            o.ClientSecret = provider.ClientSecret;
            o.ResponseType = "code";
            o.ResponseMode = "query";
            o.UsePkce = true;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.CallbackPath = provider.CallbackPath;
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

            var redirectUri = settings.RedirectUri(provider);
            o.Events.OnRedirectToIdentityProvider = context =>
            {
                // Behind a TLS-terminating proxy the request looks like plain HTTP; the callback must
                // be the public URL.
                context.ProtocolMessage.RedirectUri = redirectUri;
                return Task.CompletedTask;
            };
            o.Events.OnTicketReceived = async context =>
            {
                // "Add a login" from /account is its own path: it never signs anyone in, so the
                // sign-in rules (email matching, admission) don't apply to it.
                if (AddLogin.IsAddLogin(context.Properties))
                {
                    await AddLogin.HandleCallbackAsync(context, provider);
                    return;
                }

                var signIn = context.HttpContext.RequestServices.GetRequiredService<PersonSignIn>();
                var person = await signIn.SignInAsync(provider, ProviderIdentity.FromClaims(context.Principal!), context.HttpContext.RequestAborted);
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(SessionAuthenticationHandler.PersonIdClaim, person.Id.ToString())],
                    provider.Scheme));
            };
            o.Events.OnRemoteFailure = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(SignInFailure.LoggerCategory);
                context.Response.Redirect(SignInFailure.RemoteFailed(logger, provider, context.Failure));
                context.HandleResponse();
                return Task.CompletedTask;
            };
        }
    }
}
