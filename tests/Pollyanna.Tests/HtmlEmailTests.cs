using AngleSharp.Dom;
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
    public void The_message_is_the_plain_text_then_the_html_related_to_its_inline_bow()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
        var mail = new OutgoingMail("sue@example.com", "Gifts 2026: names are drawn",
            "Names are drawn — you can see who you're giving to.\n\nhttps://pollyanna.test/events/1\n",
            "<!DOCTYPE html><html lang=\"en\"><body><img src=\"cid:bow@test\" alt=\"\"><p>Names are drawn.</p></body></html>",
            new InlineImage("bow@test", "bow.png", png));

        var message = SmtpMailSender.Build(mail, MailboxAddress.Parse("Pollyanna <pollyanna@pollyanna.test>"));

        Assert.Equal("sue@example.com", message.To.Mailboxes.Single().Address);
        Assert.Equal("pollyanna@pollyanna.test", message.From.Mailboxes.Single().Address);
        Assert.Equal("Gifts 2026: names are drawn", message.Subject);
        // multipart/alternative: the plain text first, then multipart/related (spec §9, §15).
        var alternative = Assert.IsType<MultipartAlternative>(message.Body);
        Assert.Collection(alternative,
            plain =>
            {
                var part = Assert.IsType<TextPart>(plain);
                Assert.Equal("text/plain", part.ContentType.MimeType);
                Assert.Equal("utf-8", part.ContentType.Charset, ignoreCase: true);
                Assert.Equal(mail.Body, part.Text);
            },
            related =>
            {
                var parts = Assert.IsType<MultipartRelated>(related);
                Assert.Collection(parts,
                    html =>
                    {
                        var part = Assert.IsType<TextPart>(html);
                        Assert.Equal("text/html", part.ContentType.MimeType);
                        Assert.Equal("utf-8", part.ContentType.Charset, ignoreCase: true);
                        Assert.Equal(mail.Html, part.Text);
                    },
                    image =>
                    {
                        var part = Assert.IsType<MimePart>(image);
                        Assert.Equal("image/png", part.ContentType.MimeType);
                        Assert.Equal("bow@test", part.ContentId);
                        Assert.Equal(ContentDisposition.Inline, part.ContentDisposition!.Disposition);
                        Assert.Equal("bow.png", part.ContentDisposition!.FileName);
                        using var bytes = new MemoryStream();
                        part.Content!.DecodeTo(bytes);
                        Assert.Equal(png, bytes.ToArray());
                    });
            });

        // The same structure survives being written out and read back, as a relay sees it.
        using var wire = new MemoryStream();
        message.WriteTo(wire);
        wire.Position = 0;
        var read = MimeMessage.Load(wire);
        var readRelated = Assert.IsType<MultipartRelated>(Assert.IsType<MultipartAlternative>(read.Body)[1]);
        Assert.Equal("bow@test", readRelated[1].ContentId);
    }

    static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>Text with every run of whitespace made one space.</summary>
    static string Normalize(string text) => string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t', '\u00a0'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The plain text's link: the line between its text and its footer.</summary>
    static string PlainLink(string body) => Assert.Single(body.Split('\n'), l => l.StartsWith(TestApp.PublicUrl + "/events/"));

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

            // Nothing remote (spec §15): one image, the bow, carried inside the message.
            var img = Assert.Single(doc.QuerySelectorAll("img"));
            Assert.Equal("cid:" + mail.Image.ContentId, img.GetAttribute("src"));
            Assert.Equal("", img.GetAttribute("alt"));
            Assert.Equal(("26", "26"), (img.GetAttribute("width"), img.GetAttribute("height")));
            Assert.Equal(EmailBow.Image, mail.Image);
            Assert.Empty(doc.QuerySelectorAll("link, script, iframe, object, embed, video, audio, source, svg, picture"));
            Assert.Equal([img], doc.QuerySelectorAll("[src]"));
            Assert.Empty(doc.QuerySelectorAll("[background], [srcset]"));
            Assert.DoesNotContain("data:", mail.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("url(", mail.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@import", mail.Html, StringComparison.OrdinalIgnoreCase);
            var links = doc.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
            Assert.NotEmpty(links);
            Assert.All(links, href => Assert.StartsWith(TestApp.PublicUrl + "/", href));
        }
    }

    /// <summary>
    /// The invisible characters the preview's spacer run is made of: &amp;#847; and &amp;zwnj;, which
    /// only it uses, and &amp;nbsp;.
    /// </summary>
    static readonly char[] Spacers = ['\u034F', '\u200C', '\u00A0'];

    /// <summary>Hidden from view in every client: no box, no height, no ink, and Outlook's own switch.</summary>
    static void AssertHidden(IElement element, string because)
    {
        var style = (element.GetAttribute("style") ?? "").Replace(" ", "");
        foreach (var rule in new[] { "display:none", "max-height:0", "overflow:hidden", "opacity:0", "mso-hide:all" })
            Assert.True(style.Contains(rule), $"No {rule} on {element.OuterHtml} in {because}");
    }

    [Fact]
    public async Task Every_kind_opens_with_its_text_as_hidden_preview_text_then_a_hidden_spacer_run()
    {
        await using var app = await TestApp.StartAsync(mail: true, providers: NotificationSecrecyTests.OpenProvider);
        var run = await NotificationSecrecyTests.EveryKindAsync(app);

        foreach (var (step, mail) in run.Mail)
        {
            var because = $"the {step} email to {mail.To}:\n{mail.Html}";
            var doc = Parse(mail.Html);
            // The kind's text: the plain text before its link (spec §15).
            var plainLink = PlainLink(mail.Body);
            var kindText = Normalize(mail.Body[..mail.Body.IndexOf("\n\n" + plainLink, StringComparison.Ordinal)]);

            // First inside the wrapper, which webmail keeps when it strips <html> and <body>.
            var preview = doc.Body!.FirstElementChild!.FirstElementChild!;
            Assert.True(preview.GetAttribute("aria-hidden") == "true", because);
            AssertHidden(preview, because);
            Assert.True(kindText == Normalize(preview.TextContent), because);

            var spacer = preview.NextElementSibling!;
            Assert.True(spacer.GetAttribute("aria-hidden") == "true", because);
            AssertHidden(spacer, because);
            // Long enough to fill a wide inbox row's snippet.
            Assert.True(spacer.TextContent.Length >= 200, because);
            Assert.True(spacer.TextContent.All(c => Spacers.Contains(c)), because);

            // The plain text gets no preview line and no spacers.
            Assert.True(mail.Body.IndexOfAny(Spacers[..2]) < 0, $"Spacers in the plain text of {because}");
        }
    }

    /// <summary>The six "Tablecloth" colors (spec §12), and the only ones an email may use.</summary>
    static readonly string[] Palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];

    const string PaleBlue = "#DCE7EE", White = "#FFFFFF";

    /// <summary>An element's own background color, from its inline style, or null.</summary>
    static string? Background(IElement element) =>
        System.Text.RegularExpressions.Regex.Match(element.GetAttribute("style") ?? "", @"background-color:\s*(#[0-9A-Fa-f]{6})") is { Success: true } m
            ? m.Groups[1].Value.ToUpperInvariant() : null;

    [Fact]
    public async Task Every_kind_is_the_panel_on_the_band_with_no_stamp()
    {
        await using var app = await TestApp.StartAsync(mail: true, providers: NotificationSecrecyTests.OpenProvider);
        var run = await NotificationSecrecyTests.EveryKindAsync(app);

        foreach (var (step, mail) in run.Mail)
        {
            var because = $"the {step} email to {mail.To}:\n{mail.Html}";
            var doc = Parse(mail.Html);

            // Only the six colors, all solid; the email-only softened ink is gone (spec §15).
            var colors = System.Text.RegularExpressions.Regex.Matches(mail.Html, @"(?<!&)#[0-9A-Fa-f]{3,8}\b")
                .Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
            Assert.True(colors.All(Palette.Contains), $"Colors {string.Join(", ", colors)} in {because}");
            Assert.DoesNotContain("rgba(", mail.Html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gradient", mail.Html, StringComparison.OrdinalIgnoreCase);

            // The system font stack everywhere (Figtree can't be loaded).
            var fonts = doc.QuerySelectorAll("[style*='font-family']")
                .Select(e => System.Text.RegularExpressions.Regex.Match(e.GetAttribute("style")!, "font-family:([^;]+)").Groups[1].Value.Trim())
                .Distinct().ToList();
            Assert.True(fonts.Count == 1 && fonts[0].StartsWith("-apple-system,", StringComparison.Ordinal)
                && fonts[0].EndsWith("Arial, sans-serif", StringComparison.Ordinal), $"Fonts {string.Join(" | ", fonts)} in {because}");

            // The wordmark on white: the bow, then "pollyanna" in lowercase text.
            var bow = doc.QuerySelector("img")!;
            var wordmark = bow.Closest("table")!;
            Assert.True(Normalize(wordmark.TextContent) == "pollyanna", because);
            Assert.True(wordmark.Ancestors<IElement>().Select(Background).First(b => b is not null) == White, because);

            // One pale-blue band, holding one white panel and no text of its own.
            var band = Assert.Single(doc.QuerySelectorAll("td"), td => Background(td) == PaleBlue);
            var panel = Assert.Single(band.QuerySelectorAll("td"), td => Background(td) == White);
            Assert.True(Normalize(band.TextContent) == Normalize(panel.TextContent), $"Text on the band in {because}");
            // The panel: the event's name as its heading, the text, the button and the URL.
            Assert.True(Normalize(panel.QuerySelector("h1")!.TextContent) == "Gifts 2026", because);
            Assert.True(panel.QuerySelector("a")!.GetAttribute("href") == PlainLink(mail.Body), because);
            Assert.True(Assert.Single(doc.QuerySelectorAll("h1")) == panel.QuerySelector("h1"), because);

            // The footer sits on white under the band, outside the panel.
            var footer = doc.QuerySelectorAll("p").Last();
            Assert.False(band.Contains(footer), because);
            Assert.True(footer.Ancestors<IElement>().Select(Background).First(b => b is not null) == White, because);

            // No status stamp, in any case or form.
            Assert.Empty(doc.QuerySelectorAll(".stamp"));
            foreach (var status in new[] { "DRAFT", "OPEN", "DRAWN", "REVEALED" })
                Assert.False(doc.Body!.TextContent.Contains(status, StringComparison.Ordinal), $"Stamp {status} in {because}");
            Assert.DoesNotContain("rotate", mail.Html);
        }
    }

    /// <summary>The button's label, by where the kind links to (spec §15).</summary>
    static string ButtonLabel(NotificationKind kind) => kind switch
    {
        NotificationKind.MadeOrganizer or NotificationKind.RedrawNeeded => "Open the manage page",
        NotificationKind.Question => "Read the question",
        _ => "Open the event",
    };

    [Fact]
    public async Task Every_kinds_button_is_labeled_by_where_it_goes_and_links_to_the_url_printed_under_it()
    {
        await using var app = await TestApp.StartAsync(mail: true, providers: NotificationSecrecyTests.OpenProvider);
        var run = await NotificationSecrecyTests.EveryKindAsync(app);

        foreach (var (step, mail) in run.Mail)
        {
            var because = $"the {step} email to {mail.To}:\n{mail.Html}";
            var doc = Parse(mail.Html);
            var plainLink = PlainLink(mail.Body);

            var button = Assert.Single(doc.QuerySelectorAll("a"), a => a.GetAttribute("href") == plainLink);
            Assert.True(ButtonLabel(NotificationSecrecyTests.KindOf(mail)) == Normalize(button.TextContent), because);
            // The URL in small type under the button, as text for clients where the button fails.
            Assert.True(doc.Body!.Descendants<IText>().Any(t => t.Text.Trim() == plainLink), because);
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
