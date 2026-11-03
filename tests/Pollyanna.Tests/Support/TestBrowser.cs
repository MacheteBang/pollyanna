using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Pollyanna.MockIssuer;
using Microsoft.AspNetCore.WebUtilities;

namespace Pollyanna.Tests.Support;

/// <summary>
/// A tiny browser: keeps Pollyanna's cookies, follows redirects by hand across Pollyanna and the
/// mock issuer, and fills in the issuer's sign-in form.
/// </summary>
public sealed class TestBrowser(HttpClient app, HttpClient issuer) : IDisposable
{
    public HttpClient App => app;

    /// <summary>The redirect Pollyanna sent to the issuer during the last sign-in.</summary>
    public Uri? LastAuthorizeRequest { get; private set; }

    public Task<HttpResponseMessage> GetAsync(string pathAndQuery) => app.GetAsync(pathAndQuery);

    /// <summary>Follows redirects within Pollyanna until a non-redirect response.</summary>
    public async Task<HttpResponseMessage> GetFollowingAsync(string pathAndQuery)
    {
        var response = await app.GetAsync(pathAndQuery);
        for (var i = 0; i < 10 && IsRedirect(response) && IsPollyanna(response.Headers.Location!); i++)
            response = await app.GetAsync(Local(response.Headers.Location!));
        return response;
    }

    public async Task<IHtmlDocument> GetPageAsync(string path)
    {
        var response = await app.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ParseAsync(response);
    }

    public static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage response) =>
        new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// Requests <paramref name="startPath"/> while signed out and goes through the whole OIDC
    /// flow as <paramref name="identity"/>. Returns Pollyanna's final response to the callback
    /// (a redirect back to the page first asked for).
    /// </summary>
    public async Task<HttpResponseMessage> SignInAsync(MockIdentity identity, string startPath = "/")
    {
        var response = await app.GetAsync(startPath);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var login = response.Headers.Location!;
        Assert.StartsWith("/auth/login", login.OriginalString);

        response = await app.GetAsync(login);
        return await CompleteAtIssuerAsync(response, identity);
    }

    /// <summary>Takes Pollyanna's redirect to the issuer, signs in there and returns to Pollyanna.</summary>
    public async Task<HttpResponseMessage> CompleteAtIssuerAsync(HttpResponseMessage redirectToIssuer, MockIdentity identity, string action = "sign-in") =>
        await app.GetAsync(Local(await AuthorizeAtIssuerAsync(redirectToIssuer, identity, action)));

    /// <summary>Signs in at the issuer and returns the callback URL, without visiting it.</summary>
    public async Task<Uri> AuthorizeAtIssuerAsync(HttpResponseMessage redirectToIssuer, MockIdentity identity, string action = "sign-in")
    {
        Assert.Equal(HttpStatusCode.Redirect, redirectToIssuer.StatusCode);
        var authorize = redirectToIssuer.Headers.Location!;
        Assert.Equal(TestApp.Issuer + "/authorize", authorize.GetLeftPart(UriPartial.Path));
        LastAuthorizeRequest = authorize;

        var form = QueryHelpers.ParseQuery(authorize.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        form["sub"] = identity.Sub;
        form["name"] = identity.Name ?? "";
        form["email"] = identity.Email ?? "";
        form["email_verified"] = identity.EmailVerified is { } v ? (v ? "true" : "false") : "omit";
        form["picture"] = identity.Picture ?? "";
        form["action"] = action;

        var atIssuer = await issuer.PostAsync("/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, atIssuer.StatusCode);
        var callback = atIssuer.Headers.Location!;
        Assert.True(IsPollyanna(callback), $"Issuer redirected to {callback}");
        return callback;
    }

    /// <summary>
    /// Posts a form found on <paramref name="page"/>, antiforgery token included. Its hidden
    /// inputs are sent unless <paramref name="fields"/> names them; a field may repeat (checkboxes).
    /// With several forms posting to the same place, <paramref name="match"/> picks one.
    /// </summary>
    public Task<HttpResponseMessage> SubmitAsync(IHtmlDocument page, string formAction,
        IEnumerable<KeyValuePair<string, string>>? fields = null, Func<IHtmlFormElement, bool>? match = null)
    {
        var form = page.Forms.FirstOrDefault(f => f.GetAttribute("action") == formAction && (match?.Invoke(f) ?? true))
            ?? throw new InvalidOperationException($"No form posting to {formAction}.");
        var given = (fields ?? []).ToList();
        var values = form.Elements.OfType<IHtmlInputElement>()
            .Where(i => i.Name is not null && i.Type == "hidden" && !given.Any(g => g.Key == i.Name))
            .Select(i => KeyValuePair.Create(i.Name!, i.Value))
            .Concat(given);
        return app.PostAsync(formAction, new FormUrlEncodedContent(values));
    }

    /// <summary>Loads <paramref name="path"/> and posts one of its forms, as a person would.</summary>
    public async Task<HttpResponseMessage> SubmitOnAsync(string path, string formAction,
        IEnumerable<KeyValuePair<string, string>>? fields = null, Func<IHtmlFormElement, bool>? match = null) =>
        await SubmitAsync(await GetPageAsync(path), formAction, fields, match);

    /// <summary>Posts straight to <paramref name="formAction"/> with a valid token borrowed from another page.</summary>
    public async Task<HttpResponseMessage> PostWithTokenAsync(string tokenPage, string formAction, IEnumerable<KeyValuePair<string, string>>? fields = null)
    {
        var page = await GetPageAsync(tokenPage);
        var token = page.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
        return await app.PostAsync(formAction, new FormUrlEncodedContent(
            (fields ?? []).Append(KeyValuePair.Create("__RequestVerificationToken", token))));
    }

    static bool IsRedirect(HttpResponseMessage r) => (int)r.StatusCode is >= 300 and < 400;

    static bool IsPollyanna(Uri location) =>
        !location.IsAbsoluteUri || location.GetLeftPart(UriPartial.Authority) == TestApp.PublicUrl;

    static string Local(Uri location) => location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;

    public void Dispose()
    {
        app.Dispose();
        issuer.Dispose();
    }
}
