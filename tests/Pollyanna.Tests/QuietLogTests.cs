using System.Diagnostics;
using Pollyanna.Configuration;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Pollyanna.Tests;

/// <summary>Logs stay quiet when nothing is wrong, and still speak up when something is (spec §8, issue #5).</summary>
public sealed class QuietLogTests
{
    [Fact]
    public async Task A_first_start_and_sign_ins_warn_about_nothing_but_the_new_draw_key()
    {
        await using var app = await TestApp.StartAsync();
        await app.PersonAsync("Carol", "carol@example.com");
        using var browser = app.NewBrowser();
        // A recreated account re-links by email: the look-up that used Take without OrderBy.
        await browser.SignInAsync(new MockIdentity("carol-new", "Carol", "carol@example.com", true, null));
        using var other = app.NewBrowser();
        await other.SignInAsync(new MockIdentity("dave", "Dave", "dave@example.com", null, null));

        var warnings = app.Logs.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.StartsWith("Created a new draw key", Assert.Single(warnings));
        // The key ring was written, so data protection had its chance to warn.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(app.DataDir, "data", "keys")));
    }

    [Fact]
    public async Task Other_ef_warnings_still_log()
    {
        await using var app = await TestApp.StartAsync();
        await app.WithDbAsync(db => db.People.Take(1).ToListAsync());

        Assert.Contains(app.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Category == "Microsoft.EntityFrameworkCore.Query" && e.Message.Contains("OrderBy"));
    }

    [Fact]
    public async Task Successful_health_checks_are_not_logged_and_the_callback_is_named()
    {
        await using var app = await TestApp.StartAsync();
        using var browser = app.NewBrowser();
        await browser.GetAsync("/healthz");
        await browser.GetAsync("/healthz");
        await browser.SignInAsync(new MockIdentity("erin", "Erin", null, null, null));

        var access = app.Logs.Entries.Where(e => e.Category == "Pollyanna.Access").Select(e => e.Message).ToList();
        Assert.DoesNotContain(access, m => m.Contains("/healthz"));
        Assert.Contains("GET /auth/callback 302", access);
        Assert.DoesNotContain(access, m => m.Contains("(static) 302"));
    }

    [Fact]
    public async Task A_settings_error_prints_only_the_list_without_a_stack_trace()
    {
        var dir = Directory.CreateTempSubdirectory("pollyanna-run-").FullName;
        try
        {
            var blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "");
            var start = new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "Pollyanna.dll")])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir,
            };
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("POLLYANNA_") || k.StartsWith("ASPNETCORE_")).ToList())
                start.Environment.Remove(key);
            start.Environment[PollyannaSettings.PublicUrlKey] = "https://pollyanna.example";
            start.Environment[PollyannaSettings.IssuerKey] = "https://id.example";
            start.Environment[PollyannaSettings.ClientIdKey] = "pollyanna";
            start.Environment[PollyannaSettings.ClientSecretKey] = "s3cret";
            start.Environment[PollyannaSettings.DataDirKey] = Path.Combine(dir, "data");
            // Found by the startup check, which used to run inside the host and fail its start.
            start.Environment[PollyannaSettings.DrawKeyFileKey] = Path.Combine(blocker, "draw.key");

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var all = await output + error;

            Assert.Equal(1, process.ExitCode);
            Assert.StartsWith("Pollyanna is not configured correctly:", error);
            Assert.Contains("draw key", error);
            Assert.DoesNotContain("Hosting failed to start", all);
            Assert.DoesNotContain("   at ", all);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
