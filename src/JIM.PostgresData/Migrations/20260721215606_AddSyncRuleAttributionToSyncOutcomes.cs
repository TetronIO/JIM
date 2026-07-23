using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRuleAttributionToSyncOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SyncRuleId",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncRuleName",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncRuleId",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");

            migrationBuilder.DropColumn(
                name: "SyncRuleName",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");
        }
    }
}
