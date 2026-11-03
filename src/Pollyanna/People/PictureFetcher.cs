namespace Pollyanna.People;

public sealed record FetchedPicture(byte[] Data, string ContentType);

/// <summary>
/// Downloads a profile picture on sign-in (spec §3): http/https only, 5-second timeout, at most
/// 1 MB, and only PNG, JPEG, WebP or GIF as judged by the bytes themselves. Returns null on any
/// failure.
/// </summary>
public sealed class PictureFetcher(HttpClient http, ILogger<PictureFetcher> logger)
{
    public const int MaxBytes = 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<FetchedPicture?> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogInformation("Skipped a profile picture: not an http or https URL.");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Profile picture fetch returned {Status}.", (int)response.StatusCode);
                return null;
            }
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                logger.LogInformation("Profile picture is larger than 1 MB.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token)) > 0)
                total += read;
            if (total > MaxBytes)
            {
                logger.LogInformation("Profile picture is larger than 1 MB.");
                return null;
            }

            var data = buffer[..total];
            if (Sniff(data) is not { } contentType)
            {
                logger.LogInformation("Profile picture is not PNG, JPEG, WebP or GIF.");
                return null;
            }
            return new FetchedPicture(data, contentType);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Profile picture fetch failed ({Error}).", e.GetType().Name);
            return null;
        }
    }

    /// <summary>The image type from its magic bytes, or null if it isn't one we accept.</summary>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
            return "image/gif";
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }
}
