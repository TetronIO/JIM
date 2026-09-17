using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddAttributeValueChangeSyncRuleAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SyncRuleId",
                table: "PendingExportAttributeValueChanges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncRuleName",
                table: "PendingExportAttributeValueChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContributedBySyncRuleId",
                table: "MetaverseObjectChangeAttributeValues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContributedBySyncRuleName",
                table: "MetaverseObjectChangeAttributeValues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SyncRuleId",
                table: "ConnectedSystemObjectChangeAttributeValues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncRuleName",
                table: "ConnectedSystemObjectChangeAttributeValues",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChangeAttributeValues_ContributedBySyncRuleId",
                table: "MetaverseObjectChangeAttributeValues",
                column: "ContributedBySyncRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChangeAttributeValues_SyncRules_ContributedB~",
                table: "MetaverseObjectChangeAttributeValues",
                column: "ContributedBySyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChangeAttributeValues_SyncRules_ContributedB~",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChangeAttributeValues_ContributedBySyncRuleId",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "SyncRuleId",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "SyncRuleName",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.DropColumn(
                name: "ContributedBySyncRuleId",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "ContributedBySyncRuleName",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "SyncRuleId",
                table: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "SyncRuleName",
                table: "ConnectedSystemObjectChangeAttributeValues");
        }
    }
}
