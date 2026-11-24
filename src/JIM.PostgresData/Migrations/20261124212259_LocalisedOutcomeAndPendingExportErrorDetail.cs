using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class LocalisedOutcomeAndPendingExportErrorDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastErrorMessageDetailJson",
                table: "PendingExports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetailMessageDetailJson",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastErrorMessageDetailJson",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "DetailMessageDetailJson",
                table: "ActivityRunProfileExecutionItemSyncOutcomes");
        }
    }
}
