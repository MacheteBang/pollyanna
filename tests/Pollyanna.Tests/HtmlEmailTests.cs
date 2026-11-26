using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Notifications;
using Pollyanna.Tests.Support;
using MimeKit;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>The HTML part of every email (spec §9, §11, §15).</summary>
public sealed class HtmlEmailTests
{
    [Fact]
    public void The_message_is_multipart_alternative_with_the_plain_text_first_and_the_html_second()
    {
        var mail = new OutgoingMail("sue@example.com", "Gifts 2026: names are drawn",
            "Names are drawn — your slip is ready.\n\nhttps://pollyanna.test/events/1\n",
            "<!DOCTYPE html><html lang=\"en\"><body><p>Names are drawn — your slip is ready.</p></body></html>");

        var message = SmtpMailSender.Build(mail, MailboxAddress.Parse("Pollyanna <pollyanna@pollyanna.test>"));

        Assert.Equal("sue@example.com", message.To.Mailboxes.Single().Address);
        Assert.Equal("pollyanna@pollyanna.test", message.From.Mailboxes.Single().Address);
        Assert.Equal("Gifts 2026: names are drawn", message.Subject);
        var alternative = Assert.IsType<MultipartAlternative>(message.Body);
        Assert.Collection(alternative,
            plain =>
            {
                var part = Assert.IsType<TextPart>(plain);
                Assert.Equal("text/plain", part.ContentType.MimeType);
                Assert.Equal("utf-8", part.ContentType.Charset, ignoreCase: true);
                Assert.Equal(mail.Body, part.Text);
            },
            html =>
            {
                var part = Assert.IsType<TextPart>(html);
                Assert.Equal("text/html", part.ContentType.MimeType);
                Assert.Equal("utf-8", part.ContentType.Charset, ignoreCase: true);
                Assert.Equal(mail.Html, part.Text);
            });
    }

    static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>Text with every run of whitespace made one space.</summary>
    static string Normalize(string text) => string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t', '\u00a0'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The plain text's lines with something on them, without the "-- " that marks its footer: each
    /// is one paragraph of the email.
    /// </summary>
    static IEnumerable<string> Paragraphs(string body) =>
        body.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && l != "-- ").Select(Normalize);

    [Fact]
    public async Task Every_kind_is_sent_with_an_html_part_that_has_the_plain_texts_words_and_loads_nothing_remote()
    {
        await using var app = await TestApp.StartAsync(mail: true, providers: NotificationSecrecyTests.OpenProvider);
        var run = await NotificationSecrecyTests.EveryKindAsync(app);

        Assert.Equal(Enum.GetValues<NotificationKind>().Order(),
            run.Mail.Select(s => NotificationSecrecyTests.KindOf(s.Mail)).Distinct().Order());
        foreach (var (step, mail) in run.Mail)
        {
            var because = $"the {step} email to {mail.To}:\n{mail.Html}";
            var doc = Parse(mail.Html);

            // Accessibility (spec §15): language and direction on <html> and again on the outer
            // wrapper, the subject as the title, and every table a layout table.
            Assert.True(mail.Html.StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase), because);
            Assert.Equal(("en", "ltr"), (doc.DocumentElement.GetAttribute("lang"), doc.DocumentElement.GetAttribute("dir")));
            var wrapper = doc.Body!.FirstElementChild!;
            Assert.Equal(("en", "ltr"), (wrapper.GetAttribute("lang"), wrapper.GetAttribute("dir")));
            Assert.Equal(mail.Subject, doc.Title);
            Assert.NotEmpty(doc.QuerySelectorAll("table"));
            Assert.All(doc.QuerySelectorAll("table"), t => Assert.Equal("presentation", t.GetAttribute("role")));

            // The same words as the plain text.
            var text = Normalize(doc.Body.TextContent);
            foreach (var paragraph in Paragraphs(mail.Body))
                Assert.True(text.Contains(paragraph), $"Missing \"{paragraph}\" in {because}");

            // Nothing remote (spec §15).
            Assert.Empty(doc.QuerySelectorAll("img, link, script, iframe, object, embed, video, audio, source"));
            Assert.Empty(doc.QuerySelectorAll("[src], [background]"));
            Assert.DoesNotContain("url(", mail.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@import", mail.Html, StringComparison.OrdinalIgnoreCase);
            var links = doc.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
            Assert.NotEmpty(links);
            Assert.All(links, href => Assert.StartsWith(TestApp.PublicUrl + "/", href));
        }
    }

    [Fact]
    public async Task Markup_in_an_event_or_persons_name_is_shown_as_typed_never_as_elements()
    {
        const string eventName = "Gifts <i>2026</i> & <script>alert(1)</script>";
        const string personName = "Pat <b onclick=\"x()\">Boss</b> & \"co\"";
        await using var app = await TestApp.StartAsync(mail: true);
        var (pat, patId) = await app.SignInAsync(new MockIdentity("pat", personName, "pat@example.com", true, null));
        var (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        var (otto, ottoId) = await app.SignInAsync(new MockIdentity("otto", "Otto", "otto@example.com", true, null));
        try
        {
            // Last year's exchange puts Otto on Pat's lists.
            await app.EventAsync("Gifts 2025", EventStatus.Archived, [patId], [patId, ottoId]);
            var ev = await app.EventAsync(eventName, EventStatus.Draft, [patId], [patId, annId]);
            var manage = $"/events/{ev}/manage";
            // "Made an organizer" names who did it; "Event is open" names nobody.
            await pat.SubmitOnAsync(manage, $"{manage}/addorganizer", Form(("personId", ottoId.ToString())));
            await pat.SubmitOnAsync(manage, $"{manage}/open");
            await app.SendMailAsync();

            var madeOrganizer = Assert.Single(app.Mail.To("otto@example.com"));
            var open = Assert.Single(app.Mail.To("ann@example.com"));
            Assert.Contains(personName, madeOrganizer.Body);
            foreach (var mail in new[] { madeOrganizer, open })
            {
                // The plain text stays as typed, unescaped.
                Assert.Contains(eventName, mail.Body);
                Assert.DoesNotContain("&lt;", mail.Body);
                Assert.DoesNotContain("&amp;", mail.Body);

                var doc = Parse(mail.Html);
                Assert.Empty(doc.QuerySelectorAll("i, b, script, [onclick]"));
                Assert.Equal(mail.Subject, doc.Title);
                Assert.Contains(eventName, doc.Body!.TextContent);
            }
            Assert.Contains(personName, Parse(madeOrganizer.Html).Body!.TextContent);
        }
        finally
        {
            pat.Dispose();
            ann.Dispose();
            otto.Dispose();
        }
    }
}
