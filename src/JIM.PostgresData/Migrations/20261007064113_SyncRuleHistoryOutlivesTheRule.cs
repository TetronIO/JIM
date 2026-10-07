using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class SyncRuleHistoryOutlivesTheRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_SyncRules_SyncRuleId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_SyncRules_SyncRuleId",
                table: "MetaverseObjectChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_SyncRules_SyncRuleId",
                table: "Activities",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_SyncRules_SyncRuleId",
                table: "MetaverseObjectChanges",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_SyncRules_SyncRuleId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_SyncRules_SyncRuleId",
                table: "MetaverseObjectChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_SyncRules_SyncRuleId",
                table: "Activities",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_SyncRules_SyncRuleId",
                table: "MetaverseObjectChanges",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");
        }
    }
}
