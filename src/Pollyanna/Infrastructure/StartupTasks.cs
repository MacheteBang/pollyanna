using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Infrastructure;

/// <summary>
/// Runs before the app takes requests: checks the configuration, loads or creates the draw key,
/// and brings the database schema up to date.
/// </summary>
public sealed class StartupTasks(IServiceProvider services, ILogger<StartupTasks> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<PollyannaSettings>();
        var drawKey = CheckConfiguration(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PollyannaDb>();
        await db.Database.MigrateAsync(cancellationToken);
        if (drawKey.Created)
            await ReportNewDrawKeyAsync(scope.ServiceProvider.GetRequiredService<SealedHat>(), settings.DrawKeyFile, cancellationToken);
        if (settings.Mail is null)
            logger.LogInformation("Email notifications are off: {Key} is not set.", PollyannaSettings.SmtpHostKey);
        logger.LogInformation("Pollyanna {Version} is ready at {PublicUrl}; OIDC redirect URI: {CallbackUrl}",
            Help.Version, settings.PublicUrl, settings.CallbackUrl);
    }

    /// <summary>
    /// Loads the settings and the draw key and checks the data directory, throwing one
    /// <see cref="PollyannaConfigurationException"/> that lists every problem. Program.cs calls it
    /// before the host starts; <see cref="StartAsync"/> calls it again, which is cheap once loaded.
    /// </summary>
    public static DrawKey CheckConfiguration(IServiceProvider services)
    {
        var settings = services.GetRequiredService<PollyannaSettings>();
        var problems = new List<string>();
        try
        {
            // SQLite writes its -wal and -shm files beside the database, so the directory itself
            // must be writable, not just the file.
            Directory.CreateDirectory(settings.DataDir);
            var probe = Path.Combine(settings.DataDir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{PollyannaSettings.DataDirKey} ({settings.DataDir}) must be a directory the app can write to ({e.GetType().Name}).");
        }
        DrawKey? drawKey = null;
        try
        {
            drawKey = services.GetRequiredService<DrawKey>();
        }
        catch (PollyannaConfigurationException e)
        {
            problems.AddRange(e.Problems);
        }
        if (problems.Count > 0)
            throw new PollyannaConfigurationException(problems);
        return drawKey!;
    }

    /// <summary>
    /// A new key is expected once in an installation's life. Created while sealed draws exist, it
    /// means the old key went missing: say so loudly, but keep starting so organizers can redraw.
    /// </summary>
    async Task ReportNewDrawKeyAsync(SealedHat hat, string path, CancellationToken cancellationToken)
    {
        // Revealed and archived events only lose their "who drew whom" givers; drawn ones lose the slips.
        var sealedEvents = await hat.DrawnEventsWithSlipsAsync(cancellationToken);
        if (sealedEvents == 0)
            logger.LogWarning(
                "Created a new draw key at {Path}. Back it up now, separately from the data backups: if it's lost, "
                + "drawn but unrevealed slips can't be read and the only recovery is a redraw.", path);
        else
            logger.LogError(
                "Created a new draw key at {Path}, but {Count} drawn event(s) have slips sealed with a different key. Those draws "
                + "can't be read and organizers must redraw. If you have the old key, stop Pollyanna and put it back.",
                path, sealedEvents);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
