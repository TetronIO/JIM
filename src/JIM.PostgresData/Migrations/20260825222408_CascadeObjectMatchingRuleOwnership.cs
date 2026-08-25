using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CascadeObjectMatchingRuleOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules",
                column: "ConnectedSystemObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules",
                column: "ConnectedSystemObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");
        }
    }
}
