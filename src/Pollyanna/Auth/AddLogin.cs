using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Pollyanna.Auth;

/// <summary>
/// "Add a login" from <c>/account</c> (spec §3, §7): the challenge carries the intent and the
/// signed-in person in the protected authentication properties, and the callback attaches the
/// login to that person instead of signing anyone in. This path never goes through sign-in
/// resolution, so neither email matching nor an open provider's admission applies to it.
/// </summary>
public static class AddLogin
{
    const string IntentItem = "pollyanna:intent";
    const string IntentValue = "add-login";
    const string PersonItem = "pollyanna:person";
    const string AccountPage = "/account";

    /// <summary>The properties for a challenge that adds a login to <paramref name="personId"/>.</summary>
    public static AuthenticationProperties Properties(Guid personId) => new(new Dictionary<string, string?>
    {
        [IntentItem] = IntentValue,
        [PersonItem] = personId.ToString(),
    })
    { RedirectUri = AccountPage };

    /// <summary>Whether the returning flow was started by an "Add" button.</summary>
    public static bool IsAddLogin(AuthenticationProperties? properties) =>
        properties?.Items.TryGetValue(IntentItem, out var intent) == true && intent == IntentValue;

    /// <summary>
    /// The callback of an "Add" flow: checks that the session still belongs to the person who
    /// started it, attaches the login, and returns to <c>/account</c> with a notice. The current
    /// session carries on; no new one is made.
    /// </summary>
    public static async Task HandleCallbackAsync(TicketReceivedContext context, Provider provider)
    {
        var http = context.HttpContext;
        context.HandleResponse();

        var started = context.Properties!.Items.TryGetValue(PersonItem, out var p) && Guid.TryParse(p, out var id) ? id : (Guid?)null;
        var session = await http.AuthenticateAsync(SessionAuthenticationHandler.SchemeName);
        var current = session.Succeeded ? session.Principal.PersonId() : (Guid?)null;
        if (started is null || current != started)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(SignInFailure.LoggerCategory).LogWarning(
                "Adding a login through provider {Provider} was dropped: the session no longer belongs to the person who started it.",
                provider.Key);
            http.Response.Redirect(SignInFailure.PagePath);
            return;
        }

        var signIn = http.RequestServices.GetRequiredService<PersonSignIn>();
        var result = await signIn.AddLoginAsync(provider, started.Value, ProviderIdentity.FromClaims(context.Principal!), http.RequestAborted);

        var tempData = http.RequestServices.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(http);
        tempData[result == AddLoginResult.AnotherPersons ? "Error" : "Notice"] = result switch
        {
            AddLoginResult.Added => "Added.",
            AddLoginResult.AlreadyYours => "That's already one of your ways to sign in.",
            _ => "That sign-in already belongs to another Pollyanna account.",
        };
        tempData.Save();
        http.Response.Redirect(AccountPage);
    }
}
