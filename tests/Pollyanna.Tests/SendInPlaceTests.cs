using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Sending without the page moving (spec §16 Threads; owner, November 27, 2026). With the script,
/// the question box, the reply box and closing or reopening post in place: the same POST and the
/// redirect's GET as without it, read by site.js, which patches the thread and the composer and
/// leaves the page's scroll alone. These tests cover what the server gives the script to work
/// with: the forms it takes over, the slot an error shows in, and the page a send comes back to.
/// The script itself is checked by hand (§16 Testing).
/// </summary>
public sealed class SendInPlaceTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!, ann = null!;
    Guid ev;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        Guid patId, annId;
        (pat, patId) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        var bob = await app.PersonAsync("Bob");
        // Pat gives to Ann.
        ev = await app.EventAsync("Stockings", EventStatus.Open, [bob], [patId, annId, bob]);
        await app.SealAsync(ev, Loop(patId, annId, bob));
    }

    public async Task DisposeAsync()
    {
        pat.Dispose();
        ann.Dispose();
        await app.DisposeAsync();
    }

    string Page => $"/events/{ev}";
    string Questions => $"/events/{ev}/questions";

    /// <summary>The POST's redirect, to <paramref name="to"/>, and its GET: the two requests a send makes, with or without the script.</summary>
    static async Task<AngleSharp.Html.Dom.IHtmlDocument> FollowAsync(TestBrowser browser, HttpResponseMessage posted, string to)
    {
        Assert.Equal(System.Net.HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Equal(to, posted.Headers.Location!.OriginalString);
        return await browser.GetPageAsync(to.Split('#')[0]);
    }

    /// <summary>The note comes after the box and "Send", in the form.</summary>
    static bool Under(AngleSharp.Dom.IElement form, AngleSharp.Dom.IElement note) =>
        note.ParentElement == form
        && (form.QuerySelector("button")!.CompareDocumentPosition(note) & AngleSharp.Dom.DocumentPositions.Following) != 0
        && (form.QuerySelector("textarea")!.CompareDocumentPosition(note) & AngleSharp.Dom.DocumentPositions.Following) != 0;

    [Fact]
    public async Task The_question_box_sends_in_place_with_a_slot_for_an_error_under_the_box()
    {
        var page = await pat.GetPageAsync(Page);

        var form = page.QuerySelector($"#ask form[action='{Page}/ask']")!;
        Assert.True(form.HasAttribute("data-in-place"));
        var note = form.QuerySelector("[data-composer-note]")!;
        Assert.Equal("", note.TextContent);
        Assert.Equal("alert", note.GetAttribute("role"));
        // Under the box and "Send", not above them, and not a notice.
        Assert.True(Under(form, note));
        Assert.DoesNotContain("notice", note.ClassList);
    }

    [Fact]
    public async Task The_reply_box_closing_and_reopening_send_in_place()
    {
        await pat.SubmitOnAsync(Page, $"{Page}/ask", Form(("body", "Favorite color?")));

        var page = await ann.GetPageAsync(Questions);
        var reply = page.QuerySelector("form#reply")!;
        Assert.True(reply.HasAttribute("data-in-place"));
        Assert.True(Under(reply, reply.QuerySelector("[data-composer-note][role='alert']")!));
        Assert.True(page.QuerySelector($"form[action='{Questions}/close']")!.HasAttribute("data-in-place"));

        await ann.SubmitOnAsync(Questions, $"{Questions}/close");
        page = await ann.GetPageAsync(Questions);
        Assert.True(page.QuerySelector($"form[action='{Questions}/reopen']")!.HasAttribute("data-in-place"));
    }

    [Fact]
    public async Task Other_forms_on_the_pages_still_post_as_they_always_have()
    {
        await pat.SubmitOnAsync(Page, $"{Page}/ask", Form(("body", "Favorite color?")));

        var forms = (await pat.GetPageAsync(Page)).QuerySelectorAll("main form[data-in-place]");
        Assert.Equal([$"{Page}/ask"], forms.Select(f => f.GetAttribute("action")));
    }

    [Fact]
    public async Task A_send_that_never_reached_the_server_has_its_words_from_the_server()
    {
        var page = await pat.GetPageAsync(Page);

        using var words = System.Text.Json.JsonDocument.Parse(page.QuerySelector("main")!.GetAttribute("data-live-words")!);
        Assert.Equal("Not sent. Check your connection and try again.", words.RootElement.GetProperty("notSent").GetString());
    }

    [Fact]
    public async Task A_send_comes_back_as_the_page_with_the_new_message_and_its_notice()
    {
        // What the script reads: the redirect's GET, as without the script, is a visit.
        var page = await FollowAsync(pat, await pat.SubmitOnAsync(Page, $"{Page}/ask", Form(("body", "Favorite color?"))), $"{Page}#ask");

        Assert.Equal("Favorite color?", page.QuerySelectorAll("#ask-thread .message .bubble-body").Last().TextContent);
        Assert.Equal("Sent. Your recipient won't see your name.", page.QuerySelector("main .notice")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("main .notice-error"));
    }

    [Fact]
    public async Task A_send_into_a_conversation_closed_meanwhile_comes_back_with_an_error_and_the_closed_line()
    {
        await pat.SubmitOnAsync(Page, $"{Page}/ask", Form(("body", "Favorite color?")));
        var before = await pat.GetPageAsync(Page);
        await ann.SubmitOnAsync(Questions, $"{Questions}/close");

        var page = await FollowAsync(pat, await pat.SubmitAsync(before, $"{Page}/ask", Form(("body", "Size?"))), $"{Page}#ask");

        Assert.Equal("Your recipient has closed this conversation.", page.QuerySelector("main .notice-error")!.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("#ask-closed [data-closed]"));
    }
}
