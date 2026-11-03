using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pollyanna.Data.Migrations
{
    /// <inheritdoc />
    public partial class Reveal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PairingsRevealed",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PairingsRevealed",
                table: "Events");
        }
    }
}
