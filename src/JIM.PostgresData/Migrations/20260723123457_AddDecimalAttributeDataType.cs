using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddDecimalAttributeDataType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DecimalValue",
                table: "SyncRuleScopingCriteria",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DecimalValue",
                table: "PredefinedSearchCriteria",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DecimalValue",
                table: "PendingExportAttributeValueChanges",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DecimalValue",
                table: "MetaverseObjectChangeAttributeValues",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DecimalValue",
                table: "MetaverseObjectAttributeValues",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DecimalValue",
                table: "ConnectedSystemObjectChangeAttributeValues",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DecimalValue",
                table: "ConnectedSystemObjectAttributeValues",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_DecimalValue",
                table: "MetaverseObjectAttributeValues",
                column: "DecimalValue");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_DecimalValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "DecimalValue",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "DecimalValue",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropColumn(
                name: "DecimalValue",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "DecimalValue",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "DecimalValue",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "DecimalValue",
                table: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "DecimalValue",
                table: "ConnectedSystemObjectAttributeValues");
        }
    }
}
