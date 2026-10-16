using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddJoinMatchSourceToSyncOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchedValue",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObjectMatchingRuleDescriptionSnapshot",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectMatchingRuleId",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchedValue",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");

            migrationBuilder.DropColumn(
                name: "ObjectMatchingRuleDescriptionSnapshot",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");

            migrationBuilder.DropColumn(
                name: "ObjectMatchingRuleId",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");
        }
    }
}
