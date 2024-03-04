using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncRuleStatusToEnabled : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "SyncRules");

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "SyncRules",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "SyncRules");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "SyncRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
