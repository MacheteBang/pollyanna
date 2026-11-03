using Pollyanna.Configuration;
using Microsoft.Data.Sqlite;

namespace Pollyanna.Infrastructure;

/// <summary>
/// Operator commands for the container, which has no shell, curl or sqlite3:
/// <c>Pollyanna help</c> for every setting, <c>Pollyanna healthcheck</c> for the container health
/// check and <c>Pollyanna backup &lt;file&gt;</c> for a consistent copy of the live database.
/// </summary>
public static class Commands
{
    /// <summary>
    /// Runs the command named by <paramref name="args"/>, or returns null to start the web app.
    /// Options (<c>--name=value</c>) pass through to the host's configuration.
    /// </summary>
    public static int? Run(string[] args) => Run(args, Console.Out, Console.Error);

    internal static int? Run(string[] args, TextWriter output, TextWriter error) => args switch
    {
        ["help" or "--help" or "-h"] => Help(output),
        ["healthcheck"] => HealthCheck(null),
        ["healthcheck", var url] => HealthCheck(url),
        ["backup", var file] => Backup(file),
        [var first, ..] when !first.StartsWith('-') => Usage(error),
        ["--help" or "-h", ..] => Usage(error),
        _ => null,
    };

    static int Help(TextWriter output)
    {
        output.Write(Infrastructure.Help.Text);
        output.WriteLine();
        return 0;
    }

    static int Usage(TextWriter error)
    {
        error.WriteLine("Usage: Pollyanna [help | healthcheck [url] | backup <file>]");
        error.WriteLine("Run `/app/Pollyanna help` for every setting and command.");
        return 2;
    }

    /// <summary>
    /// Requests <c>/healthz</c> on the local HTTP port (the first of <c>ASPNETCORE_HTTP_PORTS</c>,
    /// 8080 by default) and exits 0 if it answers <c>ok</c>.
    /// </summary>
    static int HealthCheck(string? url)
    {
        if (url is null)
        {
            var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "")
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "8080";
            url = $"http://127.0.0.1:{port}/healthz";
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode && body == "ok") return 0;
            Console.Error.WriteLine($"Unhealthy: {(int)response.StatusCode} from {url}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Unhealthy: {e.GetType().Name} from {url}");
        }
        return 1;
    }

    /// <summary>
    /// Writes a consistent copy of <c>pollyanna.db</c> with SQLite's online backup API while the app
    /// keeps running. The draw key is not part of it (spec §5.3).
    /// </summary>
    static int Backup(string file)
    {
        var builder = WebApplication.CreateBuilder();
        try
        {
            return Backup(PollyannaSettings.Load(builder.Configuration, builder.Environment.ContentRootPath), file, Console.Out, Console.Error);
        }
        catch (PollyannaConfigurationException e)
        {
            Console.Error.WriteLine(e.Report(builder.Configuration));
            return 1;
        }
    }

    internal static int Backup(PollyannaSettings settings, string file, TextWriter output, TextWriter error)
    {
        var target = Path.GetFullPath(file);
        if (!File.Exists(settings.DatabasePath))
        {
            error.WriteLine($"There is no database at {settings.DatabasePath} yet.");
            return 1;
        }
        if (File.Exists(target))
        {
            error.WriteLine($"{target} already exists; choose a new file name.");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = settings.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = target,
                Pooling = false,
            }.ToString());
            source.Open();
            copy.Open();
            source.BackupDatabase(copy);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Backup failed: {e.Message}");
            return 1;
        }

        output.WriteLine($"Backed up the database to {target}.");
        output.WriteLine($"Also keep {settings.KeyRingDir} (sign-in keys). Back up the draw key separately.");
        return 0;
    }
}
