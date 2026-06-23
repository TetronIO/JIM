using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRelativeDateCriteriaFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RelativeCount",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelativeDirection",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelativeUnit",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValueMode",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RelativeCount",
                table: "PredefinedSearchCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelativeDirection",
                table: "PredefinedSearchCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelativeUnit",
                table: "PredefinedSearchCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValueMode",
                table: "PredefinedSearchCriteria",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RelativeCount",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "RelativeDirection",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "RelativeUnit",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "ValueMode",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "RelativeCount",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "RelativeDirection",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "RelativeUnit",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "ValueMode",
                table: "PredefinedSearchCriteria");
        }
    }
}
