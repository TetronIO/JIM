using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddLongValueColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LongValue",
                table: "SyncRuleScopingCriteria",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LongValue",
                table: "PendingExportAttributeValueChanges",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LongValue",
                table: "MetaverseObjectAttributeValues",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LongValue",
                table: "ConnectedSystemObjectChangeAttributeValues",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LongValue",
                table: "ConnectedSystemObjectAttributeValues",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_LongValue",
                table: "MetaverseObjectAttributeValues",
                column: "LongValue");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_LongValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "LongValue",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "LongValue",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "LongValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "LongValue",
                table: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "LongValue",
                table: "ConnectedSystemObjectAttributeValues");
        }
    }
}
