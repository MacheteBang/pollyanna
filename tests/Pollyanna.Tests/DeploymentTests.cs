using Pollyanna.Auth;
using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Infrastructure;
using Pollyanna.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pollyanna.Tests;

/// <summary>What running in a container needs (milestone 6): startup checks, backup and health check commands.</summary>
public sealed class DeploymentTests
{
    [Fact]
    public async Task Backup_copies_the_live_database_and_never_overwrites()
    {
        await using var app = await TestApp.StartAsync();
        await app.PersonAsync("Carol", "carol@example.com");
        var settings = app.Services.GetRequiredService<PollyannaSettings>();
        var target = Path.Combine(app.DataDir, "backups", "pollyanna.db");

        var output = new StringWriter();
        Assert.Equal(0, Commands.Backup(settings, target, output, TextWriter.Null));
        Assert.Contains("draw key separately", output.ToString());

        await using (var copy = new SqliteConnection($"Data Source={target};Mode=ReadOnly;Pooling=False"))
        {
            await copy.OpenAsync();
            await using var command = copy.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM People";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        var error = new StringWriter();
        Assert.Equal(1, Commands.Backup(settings, target, TextWriter.Null, error));
        Assert.Contains("already exists", error.ToString());
    }

    [Fact]
    public void Health_check_fails_when_nothing_answers()
    {
        Assert.Equal(1, Commands.Run(["healthcheck", "http://127.0.0.1:9/healthz"]));
        Assert.Equal(2, Commands.Run(["backup"]));
        Assert.Null(Commands.Run([]));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_names_every_setting_and_command(string arg)
    {
        var output = new StringWriter();
        Assert.Equal(0, Commands.Run([arg], output, TextWriter.Null));
        var help = output.ToString();

        var settings = typeof(PollyannaSettings).GetFields()
            .Where(f => f.IsLiteral && f.Name.EndsWith("Key"))
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(17, settings.Count);
        Assert.All(settings, key => Assert.Contains(key, help));
        Assert.Contains("POLLYANNA_OIDC_<KEY>_ISSUER", help);
        Assert.Contains("POLLYANNA_OIDC_<KEY>_ADMISSION", help);
        Assert.Contains("POLLYANNA_OIDC_GOOGLE_LABEL", help);
        Assert.Contains("healthcheck", help);
        Assert.Contains("backup", help);
        Assert.Contains("/auth/callback", help);
        Assert.Contains("/auth/callback/<key>", help);
        Assert.Contains("1654", help);
        Assert.Contains("32 raw bytes", help);
        Assert.Contains(Help.Version, help);
    }

    [Theory]
    [InlineData("hepl")]
    [InlineData("backup", "a", "b")]
    [InlineData("--help", "extra")]
    public void Unknown_arguments_print_the_usage_and_point_at_help(params string[] args)
    {
        var error = new StringWriter();
        Assert.Equal(2, Commands.Run(args, TextWriter.Null, error));
        Assert.Contains("/app/Pollyanna help", error.ToString());
    }

    [Fact]
    public void Options_start_the_web_app()
    {
        Assert.Null(Commands.Run(["--environment=Development"], TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void Settings_errors_name_the_redirect_uri_and_point_at_help()
    {
        var e = new PollyannaConfigurationException(["POLLYANNA_DATA_DIR is not set."]);
        IConfiguration Config(string? url) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PollyannaSettings.PublicUrlKey] = url }).Build();

        var report = e.Report(Config("https://pollyanna.example/"));
        Assert.StartsWith("Pollyanna is not configured correctly:", report);
        Assert.Contains("The OIDC redirect URI to register with the provider is https://pollyanna.example/auth/callback.", report);
        Assert.EndsWith("Run `/app/Pollyanna help` for every setting.", report);

        Assert.DoesNotContain("redirect URI", e.Report(Config("https://pollyanna.example/path")));
        Assert.DoesNotContain("redirect URI", e.Report(Config(null)));
    }

    [Fact]
    public void Settings_errors_list_the_redirect_uri_of_every_known_provider()
    {
        var e = new PollyannaConfigurationException(["POLLYANNA_OIDC_WORK_LABEL is not set."]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PollyannaSettings.PublicUrlKey] = "https://pollyanna.example",
            [PollyannaSettings.IssuerKey] = "https://id.example",
            [PollyannaSettings.ProvidersKey] = "google work Bad",
        }).Build();

        var report = e.Report(config);

        Assert.Contains("The OIDC redirect URIs to register, one per provider:" + Environment.NewLine
            + "  - https://pollyanna.example/auth/callback" + Environment.NewLine
            + "  - https://pollyanna.example/auth/callback/google" + Environment.NewLine
            + "  - https://pollyanna.example/auth/callback/work" + Environment.NewLine, report);
        Assert.DoesNotContain("Bad", report.Split("one per provider")[1]);
    }

    [Fact]
    public async Task Ready_line_names_the_version_then_each_provider_and_its_redirect_uri()
    {
        await using var app = await TestApp.StartAsync(providers: [new("google", "Google", "open")]);
        var lines = app.Logs.Entries.Select(e => e.Message).SkipWhile(m => !m.StartsWith("Pollyanna ")).Take(3).ToList();
        Assert.Equal(
        [
            $"Pollyanna {Help.Version} is ready at {TestApp.PublicUrl}",
            $"sign-in provider \"issuer.test\" (default, trusted): redirect URI {TestApp.PublicUrl}/auth/callback",
            $"sign-in provider \"Google\" (google, open): redirect URI {TestApp.PublicUrl}/auth/callback/google",
        ], lines);
    }

    [Fact]
    public async Task First_start_warns_to_back_up_the_new_draw_key()
    {
        await using var app = await TestApp.StartAsync();
        var line = Assert.Single(app.Logs.Entries, e => e.Message.StartsWith("Created a new draw key")).Message;
        Assert.Contains("Back it up now, separately from the data backups", line);
        Assert.Contains("the only recovery is a redraw", line);
    }

    [Fact]
    public async Task A_new_draw_key_with_drawn_events_logs_an_error_with_the_count()
    {
        await using var app = await TestApp.StartAsync();
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var drawn = await app.EventAsync("Drawn", EventStatus.Drawn, [ann], [ann, bob]);
        await app.SealAsync(drawn, Seed.Loop(ann, bob));
        var revealed = await app.EventAsync("Revealed", EventStatus.Revealed, [ann], [ann, bob]);
        await app.SealAsync(revealed, Seed.Loop(ann, bob));
        await app.WithDbAsync(async db =>
        {
            (await db.Events.SingleAsync(e => e.Id == revealed)).Status = EventStatus.Revealed;
            await db.SaveChangesAsync();
        });

        // The key went missing: the next start creates another one over the same database.
        var settings = app.Services.GetRequiredService<PollyannaSettings>();
        var moved = new PollyannaSettings
        {
            PublicUrl = settings.PublicUrl,
            Providers = settings.Providers,
            DataDir = settings.DataDir,
            DrawKeyFile = Path.Combine(app.DataDir, "elsewhere", "draw.key"),
        };
        var logs = new LogCapture();
        await using var services = new ServiceCollection()
            .AddSingleton(moved)
            .AddSingleton(_ => DrawKey.LoadOrCreate(moved.DrawKeyFile))
            .AddDbContext<PollyannaDb>(o => o.UseSqlite($"Data Source={moved.DatabasePath}"))
            .AddScoped<SealedHat>()
            .BuildServiceProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));

        await new StartupTasks(services, factory.CreateLogger<StartupTasks>()).StartAsync(CancellationToken.None);

        var line = Assert.Single(logs.Entries, e => e.Message.StartsWith("Created a new draw key")).Message;
        Assert.Contains("but 1 drawn event(s) have slips sealed with a different key", line);
        Assert.Contains("organizers must redraw", line);
        Assert.Contains("put it back", line);
    }

    [Fact]
    public async Task Startup_lists_an_unwritable_data_directory_and_draw_key_together()
    {
        var dir = Directory.CreateTempSubdirectory("pollyanna-startup-").FullName;
        try
        {
            var blocker = Path.Combine(dir, "file");
            File.WriteAllText(blocker, "");
            var settings = new PollyannaSettings
            {
                PublicUrl = "https://pollyanna.example",
                Providers = new ProviderRegistry([]),
                DataDir = Path.Combine(blocker, "data"),
                DrawKeyFile = Path.Combine(blocker, "draw.key"),
            };
            var services = new ServiceCollection()
                .AddSingleton(settings)
                .AddSingleton(_ => DrawKey.LoadOrCreate(settings.DrawKeyFile))
                .BuildServiceProvider();

            var e = await Assert.ThrowsAsync<PollyannaConfigurationException>(() =>
                new StartupTasks(services, NullLogger<StartupTasks>.Instance).StartAsync(CancellationToken.None));
            Assert.Equal(2, e.Problems.Count);
            Assert.Contains(e.Problems, p => p.Contains(PollyannaSettings.DataDirKey));
            Assert.Contains(e.Problems, p => p.Contains("draw key"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
