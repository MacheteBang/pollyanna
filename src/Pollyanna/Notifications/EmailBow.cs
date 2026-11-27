namespace Pollyanna.Notifications;

/// <summary>
/// The bow beside the wordmark in every HTML email (spec §12, §15): the kept bow with its folds and
/// a 1.5px white edge, transparent outside it, as a 52 × 52px PNG shown at 26 × 26. It travels
/// inside the message as a CID image, so opening an email fetches nothing (docs/adr/0003).
/// </summary>
/// <remarks>
/// <c>bow.png</c> is the image checked by hand in real mail clients (#64), drawn from the bow's SVG
/// with <c>rsvg-convert -w 52 -h 52</c>. The bow is adapted from Microsoft Fluent Emoji
/// (THIRD-PARTY-NOTICES.md).
/// </remarks>
public static class EmailBow
{
    /// <summary>
    /// The same in every email, so an email's bytes depend only on what it says. It only has to be
    /// unique within the one message, which is all a <c>cid:</c> reference looks at.
    /// </summary>
    public const string ContentId = "bow@pollyanna";

    public static readonly InlineImage Image = new(ContentId, "bow.png", Load());

    static byte[] Load()
    {
        using var stream = typeof(EmailBow).Assembly.GetManifestResourceStream("Pollyanna.Notifications.bow.png")
            ?? throw new InvalidOperationException("The email bow (Notifications/bow.png) isn't embedded in the app.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
