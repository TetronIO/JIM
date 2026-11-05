using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddScopingCriterionConnectedObjectType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectedObjectTypeId",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteria_ConnectedObjectTypeId",
                table: "SyncRuleScopingCriteria",
                column: "ConnectedObjectTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_ConnectedSystemObjectTypes_Connecte~",
                table: "SyncRuleScopingCriteria",
                column: "ConnectedObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_ConnectedSystemObjectTypes_Connecte~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleScopingCriteria_ConnectedObjectTypeId",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "ConnectedObjectTypeId",
                table: "SyncRuleScopingCriteria");
        }
    }
}
