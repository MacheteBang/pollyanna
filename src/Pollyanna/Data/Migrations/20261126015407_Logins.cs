using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pollyanna.Data.Migrations;

/// <summary>
/// Several logins per person (spec §4): each person's issuer and sub move into one login. There
/// is no real way back: <see cref="Down"/> keeps only each person's earliest login.
/// </summary>
public partial class Logins : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Logins",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                Issuer = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                Subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                LastEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Logins", x => x.Id);
                table.ForeignKey(
                    name: "FK_Logins_People_PersonId",
                    column: x => x.PersonId,
                    principalTable: "People",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Logins_Issuer_Subject",
            table: "Logins",
            columns: new[] { "Issuer", "Subject" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Logins_PersonId",
            table: "Logins",
            column: "PersonId");

        // Each living person's issuer and sub become their one login, with their current email as
        // its last email. Tombstones have neither, so they get none. IDs are random GUIDs in the
        // upper-case text form EF Core writes for SQLite.
        migrationBuilder.Sql("""
            INSERT INTO Logins (Id, PersonId, Issuer, Subject, LastEmail, CreatedAt, LastUsedAt)
            SELECT hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-'
                       || hex(randomblob(2)) || '-' || hex(randomblob(6)),
                   Id, OidcIssuer, OidcSub, Email, CreatedAt, COALESCE(LastLoginAt, CreatedAt)
            FROM People
            WHERE OidcIssuer IS NOT NULL AND OidcSub IS NOT NULL;
            """);

        migrationBuilder.DropIndex(
            name: "IX_People_OidcIssuer_OidcSub",
            table: "People");

        migrationBuilder.DropColumn(
            name: "OidcIssuer",
            table: "People");

        migrationBuilder.DropColumn(
            name: "OidcSub",
            table: "People");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OidcIssuer",
            table: "People",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OidcSub",
            table: "People",
            type: "TEXT",
            nullable: true);

        // Only one login per person fits back: the earliest.
        migrationBuilder.Sql("""
            UPDATE People SET
                OidcIssuer = (SELECT Issuer FROM Logins WHERE PersonId = People.Id ORDER BY CreatedAt, Id LIMIT 1),
                OidcSub = (SELECT Subject FROM Logins WHERE PersonId = People.Id ORDER BY CreatedAt, Id LIMIT 1);
            """);

        migrationBuilder.DropTable(
            name: "Logins");

        migrationBuilder.CreateIndex(
            name: "IX_People_OidcIssuer_OidcSub",
            table: "People",
            columns: new[] { "OidcIssuer", "OidcSub" },
            unique: true);
    }
}
