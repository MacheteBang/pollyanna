using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pollyanna.Data.Migrations
{
    /// <inheritdoc />
    public partial class Draw : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WantsToLeave",
                table: "Participants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Redrawn",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GiverTag = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    Viewed = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotesVersionSeen = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipientChanged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Assignments_People_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_EventId_GiverTag",
                table: "Assignments",
                columns: new[] { "EventId", "GiverTag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_EventId_RecipientId",
                table: "Assignments",
                columns: new[] { "EventId", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_RecipientId",
                table: "Assignments",
                column: "RecipientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropColumn(
                name: "WantsToLeave",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "Redrawn",
                table: "Events");
        }
    }
}
