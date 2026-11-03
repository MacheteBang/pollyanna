using System.Security.Cryptography;

namespace Pollyanna.Configuration;

/// <summary>
/// The 32-byte key that seals the draw (spec §5.3). Created on first start if the file is
/// absent, readable only by the app's user. Never logged or shown.
/// </summary>
public sealed class DrawKey
{
    public const int Length = 32;

    readonly byte[] key;

    DrawKey(byte[] key, bool created)
    {
        this.key = key;
        Created = created;
    }

    /// <summary>True when this start created the key; startup tasks tell the operator (spec §5.3).</summary>
    public bool Created { get; }

    internal ReadOnlySpan<byte> Bytes => key;

    /// <summary>
    /// <c>HMAC-SHA256(draw_key, "giver" ‖ event_id ‖ giver_person_id)</c> (spec §5.2): what an
    /// assignment stores instead of its giver. Only someone holding the key can compute it.
    /// </summary>
    public byte[] GiverTag(Guid eventId, Guid giver)
    {
        Span<byte> data = stackalloc byte[5 + 16 + 16];
        "giver"u8.CopyTo(data);
        eventId.TryWriteBytes(data[5..], bigEndian: true, out _);
        giver.TryWriteBytes(data[21..], bigEndian: true, out _);
        return HMACSHA256.HashData(key, data);
    }

    /// <summary>Loads the key, creating it first if the file doesn't exist.</summary>
    public static DrawKey LoadOrCreate(string path)
    {
        try
        {
            return LoadOrCreateCore(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PollyannaConfigurationException([
                $"The draw key file {path} can't be created or read ({e.GetType().Name}). On first start its "
                + "directory must be writable by the app; after that the file must be readable by it."]);
        }
    }

    static DrawKey LoadOrCreateCore(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fresh = RandomNumberGenerator.GetBytes(Length);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using var stream = new FileStream(path, options);
                stream.Write(fresh);
                return new DrawKey(fresh, created: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created it first; fall through and read theirs.
            }
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != Length)
            throw new PollyannaConfigurationException([
                $"The draw key file {path} must hold exactly {Length} raw bytes (not base64 or hex); it holds {bytes.Length}. "
                + "If no draw has been made yet, delete it and Pollyanna creates a new one on start; otherwise put the original key back."]);
        return new DrawKey(bytes, created: false);
    }

    public override string ToString() => "[draw key]";
}
