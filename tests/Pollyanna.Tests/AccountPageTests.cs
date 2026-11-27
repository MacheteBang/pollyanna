using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;

namespace Pollyanna.Tests;

/// <summary>
/// The account page and the delete-account confirmation in the "Tablecloth" identity (spec §7
/// Account and Confirmations, §12, §15 Opting out): plain groups on white, no cloth, the email
/// switch that saves when flipped, outlined "Sign out", red "Delete my account".
/// </summary>
public sealed class AccountPageTests : IAsyncLifetime
{
    static readonly MockIdentity Pat = new("pat", "Pat Example", "pat@example.com", true, null);
    static readonly string[] Palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];

    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true, providers: [new("google", "Google"), new("work", "Work account")]);
        (browser, me) = await app.SignInAsync(Pat);
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    static string Squash(string text) => string.Join(" ", text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData("/account")]
    [InlineData("/account/delete")]
    public async Task Both_pages_are_on_the_tablecloth_layout_with_no_cloth(string path)
    {
        var page = await browser.GetPageAsync(path);

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
        Assert.Empty(page.QuerySelectorAll(".cloth"));
        Assert.Empty(page.QuerySelectorAll("[style], style"));
        var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
        Assert.All(hexes, h => Assert.Contains(h, Palette));
    }

    [Fact]
    public async Task The_account_page_is_titled_with_name_and_email_in_the_small_line_and_goes_back_home()
    {
        var page = await browser.GetPageAsync("/account");

        Assert.Equal("Your account", Assert.Single(page.QuerySelectorAll("main h1")).TextContent.Trim());
        Assert.Equal("Pat Example · pat@example.com", page.QuerySelector(".page-head-line")!.TextContent.Trim());
        Assert.Equal("/", page.QuerySelector(".page-head-back")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Three_groups_ways_to_sign_in_then_emails_then_sign_out_and_delete()
    {
        var page = await browser.GetPageAsync("/account");

        Assert.Equal(["Ways to sign in", "Emails"], page.QuerySelectorAll("main h2").Select(h => h.TextContent.Trim()));
        var main = page.QuerySelector("main")!;
        var signOut = main.QuerySelector("form[action='/auth/signout'] button")!;
        Assert.Equal("Sign out", signOut.TextContent.Trim());
        Assert.Contains("button", signOut.ClassList);
        Assert.DoesNotContain("button-fill", signOut.ClassList);
        Assert.DoesNotContain("button-red", signOut.ClassList);
        var delete = main.QuerySelector("a.button[href='/account/delete']")!;
        Assert.Equal("Delete my account", delete.TextContent.Trim());
        Assert.Contains("button-red", delete.ClassList);
        // Order: logins, then emails, then leaving.
        var all = main.QuerySelectorAll("#logins, #emails, form[action='/auth/signout'], a[href='/account/delete'].button").ToList();
        Assert.Equal(["logins", "emails", "FORM", "A"], all.Select(e => e.Id is { Length: > 0 } id ? id : e.TagName));
    }

    [Fact]
    public async Task The_email_switch_saves_when_flipped_with_a_save_button_for_no_script()
    {
        var page = await browser.GetPageAsync("/account");

        var form = page.QuerySelector("#emails form[action='/account/emails']")!;
        Assert.True(form.HasAttribute("data-save-on-change"));
        Assert.Equal("Save", form.QuerySelector("button[type='submit']")!.TextContent.Trim());
    }

    [Fact]
    public async Task The_script_submits_a_save_on_change_form_and_hides_its_save_button()
    {
        var script = await (await browser.GetAsync("/js/site.js")).Content.ReadAsStringAsync();

        Assert.Contains("[data-save-on-change]", script);
    }

    [Fact]
    public async Task The_delete_confirmation_asks_its_question_as_the_title_then_the_cost_then_red_delete_and_keep()
    {
        var page = await browser.GetPageAsync("/account/delete");

        Assert.Equal("Delete your account?", Assert.Single(page.QuerySelectorAll("main h1")).TextContent.Trim());
        Assert.Equal("/account", page.QuerySelector(".page-head-back")!.GetAttribute("href"));
        var main = page.QuerySelector("main")!;
        Assert.Contains("Your name, email, picture and notes are deleted", main.TextContent);
        var button = Assert.Single(main.QuerySelectorAll("form[action='/account/delete'] button"));
        Assert.Equal("Delete my account", button.TextContent.Trim());
        Assert.Contains("button-red", button.ClassList);
        Assert.DoesNotContain("button-fill", button.ClassList);
        var keep = main.QuerySelectorAll("a").Single(a => a.TextContent.Trim() == "Keep my account");
        Assert.Equal("/account", keep.GetAttribute("href"));
        // The cost comes before the button, and the way out after it.
        var text = Squash(main.TextContent);
        Assert.True(text.IndexOf("are deleted") < text.IndexOf("Delete my account"));
        Assert.True(text.IndexOf("Delete my account") < text.IndexOf("Keep my account"));
    }

    [Fact]
    public async Task When_deleting_is_blocked_there_is_no_action_only_what_to_do_first()
    {
        var ann = await app.PersonAsync("Ann");
        await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann]);

        var page = await browser.GetPageAsync("/account/delete");

        var main = page.QuerySelector("main")!;
        Assert.Equal("Delete your account?", main.QuerySelector("h1")!.TextContent.Trim());
        Assert.Contains("You're the only organizer of Stockings. Add another organizer on its Organizing page, then come back.", main.TextContent);
        Assert.DoesNotContain("manage page", main.TextContent);
        Assert.Empty(main.QuerySelectorAll("form, button, .button"));
        Assert.DoesNotContain("Your name, email, picture and notes are deleted", main.TextContent);
        Assert.Equal("/account", main.QuerySelectorAll("a").Single(a => a.TextContent.Trim() == "Back to your account").GetAttribute("href"));
    }
}
