using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddExpressionToScopingCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompareAs",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Expression",
                table: "SyncRuleScopingCriteria",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompareAs",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "Expression",
                table: "SyncRuleScopingCriteria");
        }
    }
}
