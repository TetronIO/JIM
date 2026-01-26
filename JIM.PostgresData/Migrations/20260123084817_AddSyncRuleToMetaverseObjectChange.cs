using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRuleToMetaverseObjectChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SyncRuleId",
                table: "MetaverseObjectChanges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncRuleName",
                table: "MetaverseObjectChanges",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_SyncRuleId",
                table: "MetaverseObjectChanges",
                column: "SyncRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_SyncRules_SyncRuleId",
                table: "MetaverseObjectChanges",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_SyncRules_SyncRuleId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChanges_SyncRuleId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "SyncRuleId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "SyncRuleName",
                table: "MetaverseObjectChanges");
        }
    }
}
