using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemObjectJoinRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JoinSyncRuleId",
                table: "ConnectedSystemObjects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JoinSyncRuleName",
                table: "ConnectedSystemObjects",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_JoinSyncRuleId",
                table: "ConnectedSystemObjects",
                column: "JoinSyncRuleId",
                filter: "\"JoinSyncRuleId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjects_SyncRules_JoinSyncRuleId",
                table: "ConnectedSystemObjects",
                column: "JoinSyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjects_SyncRules_JoinSyncRuleId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_JoinSyncRuleId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "JoinSyncRuleId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "JoinSyncRuleName",
                table: "ConnectedSystemObjects");
        }
    }
}
