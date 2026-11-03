using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>The email switch on /account (spec §15 "Opting out", §7).</summary>
public sealed class EmailSwitchTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    const string SwitchAction = "/account/emails";

    static AngleSharp.Html.Dom.IHtmlInputElement? Switch(AngleSharp.Html.Dom.IHtmlDocument page) =>
        page.QuerySelector($"form[action='{SwitchAction}'] input[type='checkbox'][name='on']") as AngleSharp.Html.Dom.IHtmlInputElement;

    [Fact]
    public async Task The_switch_is_on_for_someone_new()
    {
        var page = await browser.GetPageAsync("/account");

        var input = Switch(page);
        Assert.NotNull(input);
        Assert.True(input.IsChecked);
        var label = page.QuerySelector($"label[for='{input.Id}']") ?? input.Closest("label");
        Assert.Equal("Email me about Pollyanna events", label!.TextContent.Trim());
    }

    [Fact]
    public async Task Without_a_mail_relay_the_switch_is_hidden()
    {
        await using var plain = await TestApp.StartAsync();
        var (patBrowser, _) = await plain.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _b = patBrowser;

        var page = await patBrowser.GetPageAsync("/account");

        Assert.Null(page.QuerySelector($"form[action='{SwitchAction}']"));
        Assert.DoesNotContain("Email me about Pollyanna events", page.Body!.TextContent);
        var response = await patBrowser.PostWithTokenAsync("/account", SwitchAction, [KeyValuePair.Create("on", "false")]);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Saving_the_switch_needs_a_form_token()
    {
        var response = await browser.App.PostAsync(SwitchAction, new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await app.WithDbAsync(db => db.People.Where(p => p.Id == me).Select(p => p.EmailsOn).SingleAsync()));
    }

    [Fact]
    public async Task Switching_off_is_saved_and_shown()
    {
        await TurnOffAsync(browser);

        var page = await browser.GetPageAsync("/account");
        Assert.False(Switch(page)!.IsChecked);
        Assert.Contains("Emails are off.", page.Body!.TextContent);
    }

    Task OpenAsync(Guid ev) => browser.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/open");

    async Task<(TestBrowser Browser, Guid Id)> AnnAsync() =>
        await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));

    [Fact]
    public async Task Switching_off_drops_what_is_waiting_and_nothing_more_is_sent()
    {
        var (ann, annId) = await AnnAsync();
        using var _a = ann;
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId, bob]);
        await OpenAsync(ev);

        await TurnOffAsync(ann);
        await app.SendMailAsync();

        Assert.Equal(["bob@example.com"], app.Mail.Sent.Select(m => m.To));
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Turning_it_back_on_before_the_send_does_not_bring_back_what_was_waiting()
    {
        var (ann, annId) = await AnnAsync();
        using var _a = ann;
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId]);
        await OpenAsync(ev);

        await TurnOffAsync(ann);
        await TurnOnAsync(ann);
        await app.SendMailAsync();

        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task Turning_it_back_on_affects_only_later_notifications()
    {
        var (ann, annId) = await AnnAsync();
        using var _a = ann;
        var earlier = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId]);
        var later = await app.EventAsync("Cookie swap", EventStatus.Draft, [me], [me, annId]);
        await TurnOffAsync(ann);
        await OpenAsync(earlier);

        await TurnOnAsync(ann);
        await OpenAsync(later);
        await app.SendMailAsync();

        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("ann@example.com", mail.To);
        Assert.Contains("Cookie swap", mail.Subject);
    }

    [Fact]
    public async Task The_switch_is_read_when_the_email_is_sent()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, ann]);
        await OpenAsync(ev);
        Assert.Single(await app.WithDbAsync(db => db.Notifications.ToListAsync()));

        // Switched off between queueing and sending without going through the page, e.g. while
        // the sender was already working through the outbox.
        await app.WithDbAsync(db => db.People.Where(p => p.Id == ann).ExecuteUpdateAsync(s => s.SetProperty(p => p.EmailsOn, false)));
        await app.SendMailAsync();

        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task People_who_signed_up_before_the_switch_existed_have_it_on()
    {
        var path = Path.Combine(app.DataDir, "upgrade.db");
        var options = new DbContextOptionsBuilder<PollyannaDb>().UseSqlite($"Data Source={path};Pooling=False")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.NonTransactionalMigrationOperationWarning)).Options;
        await using var db = new PollyannaDb(options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261016171914_Notifications");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO People (Id, OidcIssuer, OidcSub, DisplayName, CreatedAt) VALUES ('6f1d2b8e-0000-0000-0000-000000000001', 'iss', 'old', 'Old timer', '2025-12-01 00:00:00')");

        await db.Database.MigrateAsync();

        Assert.True(await db.People.Select(p => p.EmailsOn).SingleAsync());
    }

    static async Task TurnOffAsync(TestBrowser who) =>
        Assert.Equal(HttpStatusCode.Redirect, (await who.SubmitOnAsync("/account", SwitchAction)).StatusCode);

    static async Task TurnOnAsync(TestBrowser who) =>
        Assert.Equal(HttpStatusCode.Redirect, (await who.SubmitOnAsync("/account", SwitchAction, [KeyValuePair.Create("on", "true")])).StatusCode);
}
