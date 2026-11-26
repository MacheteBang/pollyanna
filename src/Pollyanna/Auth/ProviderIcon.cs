using System.Security.Cryptography;
using System.Xml;
using Pollyanna.People;

namespace Pollyanna.Auth;

/// <summary>
/// A provider's logo, from the image file the operator names in <c>POLLYANNA_OIDC_ICON_FILE</c> or
/// <c>POLLYANNA_OIDC_&lt;KEY&gt;_ICON_FILE</c> (spec §2, §3). Read and checked once at startup, then
/// served by <see cref="ProviderIconEndpoint"/>.
/// </summary>
public sealed class ProviderIcon
{
    /// <summary>1 MB: more than any button logo needs; keeps a mistaken path from filling memory.</summary>
    public const int MaxBytes = 1024 * 1024;
    const string SvgNamespace = "http://www.w3.org/2000/svg";

    public required byte[] Data { get; init; }
    /// <summary>The checked content type: PNG, JPEG, GIF, WebP or SVG.</summary>
    public required string ContentType { get; init; }
    /// <summary>A short hash of the content, so a changed file gets a new URL.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Reads and checks the icon file named by <paramref name="setting"/>, adding a problem
    /// (naming the setting) when it can't be read or isn't a usable image.
    /// </summary>
    internal static ProviderIcon? Load(string setting, string file, string contentRoot, List<string> problems)
    {
        byte[] data;
        try
        {
            var path = Path.GetFullPath(file, contentRoot);
            if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
            {
                problems.Add($"The file named by {setting} is larger than 1 MB.");
                return null;
            }
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The file named by {setting} can't be read ({e.GetType().Name}).");
            return null;
        }

        if ((PictureFetcher.Sniff(data) ?? (IsSvg(data) ? "image/svg+xml" : null)) is not { } contentType)
        {
            problems.Add($"The file named by {setting} isn't a usable image: use PNG, JPEG, GIF, WebP or SVG.");
            return null;
        }
        return new ProviderIcon
        {
            Data = data,
            ContentType = contentType,
            Version = Convert.ToHexStringLower(SHA256.HashData(data))[..12],
        };
    }

    /// <summary>A well-formed XML document whose root is an SVG element, with no DTD.</summary>
    static bool IsSvg(byte[] data)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg" || reader.NamespaceURI != SvgNamespace)
                return false;
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
