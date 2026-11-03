using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pollyanna.Data.Migrations
{
    /// <inheritdoc />
    public partial class Events : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HouseholdId",
                table: "Participants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Exclusions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonA = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonB = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exclusions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Exclusions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Exclusions_People_PersonA",
                        column: x => x.PersonA,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Exclusions_People_PersonB",
                        column: x => x.PersonB,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Households",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Households", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Households_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    EmailNormalized = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    InvitedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invitations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Invitations_People_InvitedBy",
                        column: x => x.InvitedBy,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_HouseholdId",
                table: "Participants",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_Exclusions_EventId_PersonA_PersonB",
                table: "Exclusions",
                columns: new[] { "EventId", "PersonA", "PersonB" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exclusions_PersonA",
                table: "Exclusions",
                column: "PersonA");

            migrationBuilder.CreateIndex(
                name: "IX_Exclusions_PersonB",
                table: "Exclusions",
                column: "PersonB");

            migrationBuilder.CreateIndex(
                name: "IX_Households_EventId",
                table: "Households",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_EmailNormalized",
                table: "Invitations",
                column: "EmailNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_EventId_EmailNormalized",
                table: "Invitations",
                columns: new[] { "EventId", "EmailNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_InvitedBy",
                table: "Invitations",
                column: "InvitedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_Participants_Households_HouseholdId",
                table: "Participants",
                column: "HouseholdId",
                principalTable: "Households",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Participants_Households_HouseholdId",
                table: "Participants");

            migrationBuilder.DropTable(
                name: "Exclusions");

            migrationBuilder.DropTable(
                name: "Households");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropIndex(
                name: "IX_Participants_HouseholdId",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "HouseholdId",
                table: "Participants");
        }
    }
}
