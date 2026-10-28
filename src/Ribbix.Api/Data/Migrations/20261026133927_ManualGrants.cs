using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ribbix.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "granted_by",
                table: "payments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "payments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "granted_by",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "note",
                table: "payments");
        }
    }
}
