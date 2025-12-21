using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseSensitiveToMatchingAndScopingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CaseSensitive",
                table: "SyncRuleScopingCriteria",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CaseSensitive",
                table: "ObjectMatchingRules",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaseSensitive",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "CaseSensitive",
                table: "ObjectMatchingRules");
        }
    }
}
