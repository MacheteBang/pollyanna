using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Pollyanna.Live;
using Pollyanna.MockIssuer;
using Microsoft.AspNetCore.WebUtilities;

namespace Pollyanna.Tests.Support;

/// <summary>
/// A tiny browser: keeps Pollyanna's cookies, follows redirects by hand across Pollyanna and the
/// mock issuers (keyed by issuer URL), and fills in an issuer's sign-in form.
/// </summary>
public sealed class TestBrowser(HttpClient app, IReadOnlyDictionary<string, HttpClient> issuers, TestLive liveStreams) : IDisposable
{
    public HttpClient App => app;

    /// <summary>
    /// Opens a live stream as a page of this browser would (spec §16): one per tab, so call it
    /// twice for two tabs. Returns once the stream's opening frame is in.
    /// </summary>
    public Task<LiveReader> OpenLiveAsync() => LiveReader.OpenAsync(app, liveStreams);

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

    /// <summary>
    /// Loads <paramref name="path"/> and parses it. With <paramref name="live"/>, it is a live
    /// refetch, as the page's script makes on a hint: the same URL, marked with
    /// <see cref="LiveRefetch.Header"/>, which counts as no visit (spec §16).
    /// </summary>
    public async Task<IHtmlDocument> GetPageAsync(string path, bool live = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (live)
            request.Headers.Add(LiveRefetch.Header, LiveRefetch.CatchUp);
        var response = await app.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ParseAsync(response);
    }

    /// <summary>
    /// A live refetch of <paramref name="path"/> as the page's script makes it, not followed: on a
    /// hint (<see cref="LiveRefetch.Hint"/>, the pair parts left out) or catching up (spec §16).
    /// </summary>
    public Task<HttpResponseMessage> RefetchAsync(string path, bool hint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(LiveRefetch.Header, hint ? LiveRefetch.Hint : LiveRefetch.CatchUp);
        return app.SendAsync(request);
    }

    public static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage response) =>
        new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// Requests <paramref name="startPath"/> while signed out and goes through the whole OIDC
    /// flow as <paramref name="identity"/>, through <paramref name="provider"/> (a key; the unnamed
    /// provider by default), picking it on the chooser when several are configured. Returns
    /// Pollyanna's final response to the callback (a redirect back to the page first asked for).
    /// </summary>
    public async Task<HttpResponseMessage> SignInAsync(MockIdentity identity, string startPath = "/", string? provider = null)
    {
        var response = await app.GetAsync(startPath);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var login = response.Headers.Location!;
        Assert.StartsWith("/auth/login", login.OriginalString);

        response = await app.GetAsync(login);
        if (response.StatusCode == HttpStatusCode.OK)
            response = await app.GetAsync(await ChooseAsync(response, provider ?? "default"));
        else
            Assert.True(provider is null or "default", "Only one provider is configured, so there is no chooser.");
        return await CompleteAtIssuerAsync(response, identity);
    }

    /// <summary>The href of the chooser button that signs in through the provider <paramref name="key"/>.</summary>
    public static async Task<string> ChooseAsync(HttpResponseMessage chooser, string key) =>
        (await ParseAsync(chooser)).QuerySelectorAll("a.button").Select(a => a.GetAttribute("href")!)
            .Single(href => href == "/auth/login/" + key || href.StartsWith("/auth/login/" + key + "?"));

    /// <summary>Takes Pollyanna's redirect to the issuer, signs in there and returns to Pollyanna.</summary>
    public async Task<HttpResponseMessage> CompleteAtIssuerAsync(HttpResponseMessage redirectToIssuer, MockIdentity identity, string action = "sign-in") =>
        await app.GetAsync(Local(await AuthorizeAtIssuerAsync(redirectToIssuer, identity, action)));

    /// <summary>Signs in at the issuer and returns the callback URL, without visiting it.</summary>
    public async Task<Uri> AuthorizeAtIssuerAsync(HttpResponseMessage redirectToIssuer, MockIdentity identity, string action = "sign-in")
    {
        Assert.Equal(HttpStatusCode.Redirect, redirectToIssuer.StatusCode);
        var authorize = redirectToIssuer.Headers.Location!;
        Assert.Equal("/authorize", authorize.AbsolutePath);
        var issuer = issuers[authorize.GetLeftPart(UriPartial.Authority)];
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
        foreach (var issuer in issuers.Values)
            issuer.Dispose();
    }
}
