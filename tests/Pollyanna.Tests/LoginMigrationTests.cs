using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Pollyanna.Tests;

/// <summary>
/// Upgrading to several logins per person (spec §4): each existing person's issuer and sub
/// become one login, and they sign in as themselves afterwards. Tombstones get no login.
/// </summary>
public sealed class LoginMigrationTests
{
    const string Alice = "6F1D2B8E-0000-0000-0000-00000000000A";
    const string Gone = "6F1D2B8E-0000-0000-0000-00000000000B";

    [Fact]
    public async Task An_existing_person_signs_in_as_themselves_after_the_upgrade()
    {
        var dataDir = Directory.CreateTempSubdirectory("pollyanna-upgrade-").FullName;
        try
        {
            var options = new DbContextOptionsBuilder<PollyannaDb>()
                .UseSqlite($"Data Source={Path.Combine(dataDir, "pollyanna.db")};Pooling=False")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning)).Options;
            await using (var db = new PollyannaDb(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261016173013_EmailSwitch");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO People (Id, OidcIssuer, OidcSub, DisplayName, Email, EmailNormalized, EmailVerified, CreatedAt, LastLoginAt, EmailsOn) VALUES "
                    + $"('{Alice}', '{TestApp.Issuer}', 'alice-1', 'Alice', 'Alice@Example.com', 'alice@example.com', 1, '2025-12-01 00:00:00', '2025-12-02 00:00:00', 1), "
                    + $"('{Gone}', NULL, NULL, NULL, NULL, NULL, NULL, '1970-01-01 00:00:00', NULL, 1)");
            }

            await using var app = await TestApp.StartAsync(configure: b => b.UseSetting(PollyannaSettings.DataDirKey, dataDir));

            var logins = await app.WithDbAsync(db => db.Logins.AsNoTracking().ToListAsync());
            var login = Assert.Single(logins);
            Assert.Equal((Guid.Parse(Alice), TestApp.Issuer, "alice-1", "Alice@Example.com"), (login.PersonId, login.Issuer, login.Subject, login.LastEmail));
            Assert.Equal(new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc), login.CreatedAt);
            Assert.Equal(new DateTime(2025, 12, 2, 0, 0, 0, DateTimeKind.Utc), login.LastUsedAt);

            var (browser, id) = await app.SignInAsync(new MockIdentity("alice-1", "Alice", "alice@example.com", true, null));
            using var _ = browser;
            Assert.Equal(Guid.Parse(Alice), id);
            Assert.Equal(2, await app.WithDbAsync(db => db.People.CountAsync()));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
