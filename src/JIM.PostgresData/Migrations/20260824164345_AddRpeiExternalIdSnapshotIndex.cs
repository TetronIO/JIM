using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRpeiExternalIdSnapshotIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItems_ExternalIdSnapshot",
                table: "ActivityRunProfileExecutionItems",
                column: "ExternalIdSnapshot");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivityRunProfileExecutionItems_ExternalIdSnapshot",
                table: "ActivityRunProfileExecutionItems");
        }
    }
}
