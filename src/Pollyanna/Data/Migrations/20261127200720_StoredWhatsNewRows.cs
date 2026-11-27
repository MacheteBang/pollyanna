using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pollyanna.Data.Migrations;

/// <inheritdoc />
public partial class StoredWhatsNewRows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InAppNotifications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                NamedPersonId = table.Column<Guid>(type: "TEXT", nullable: true),
                At = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InAppNotifications", x => x.Id);
                table.ForeignKey(
                    name: "FK_InAppNotifications_Events_EventId",
                    column: x => x.EventId,
                    principalTable: "Events",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_InAppNotifications_People_NamedPersonId",
                    column: x => x.NamedPersonId,
                    principalTable: "People",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_InAppNotifications_People_PersonId",
                    column: x => x.PersonId,
                    principalTable: "People",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_InAppNotifications_EventId_Kind",
            table: "InAppNotifications",
            columns: new[] { "EventId", "Kind" });

        migrationBuilder.CreateIndex(
            name: "IX_InAppNotifications_NamedPersonId",
            table: "InAppNotifications",
            column: "NamedPersonId");

        migrationBuilder.CreateIndex(
            name: "IX_InAppNotifications_PersonId_EventId_Kind",
            table: "InAppNotifications",
            columns: new[] { "PersonId", "EventId", "Kind" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "InAppNotifications");
    }
}
