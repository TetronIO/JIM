using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncRuleMapping4Matching : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_SynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMapping_SynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.RenameColumn(
                name: "SynchronisationRuleId",
                table: "SyncRuleMapping",
                newName: "Type");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "SyncRules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMapping",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_CreatedById",
                table: "SyncRules",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMapping_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping",
                column: "AttributeFlowSynchronisationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMapping_ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMapping",
                column: "ObjectMatchingSynchronisationRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping",
                column: "AttributeFlowSynchronisationRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_ObjectMatchingSynchronisationRule~",
                table: "SyncRuleMapping",
                column: "ObjectMatchingSynchronisationRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRules_MetaverseObjects_CreatedById",
                table: "SyncRules",
                column: "CreatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_ObjectMatchingSynchronisationRule~",
                table: "SyncRuleMapping");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRules_MetaverseObjects_CreatedById",
                table: "SyncRules");

            migrationBuilder.DropIndex(
                name: "IX_SyncRules_CreatedById",
                table: "SyncRules");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMapping_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMapping_ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.DropColumn(
                name: "ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.RenameColumn(
                name: "Type",
                table: "SyncRuleMapping",
                newName: "SynchronisationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMapping_SynchronisationRuleId",
                table: "SyncRuleMapping",
                column: "SynchronisationRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_SynchronisationRuleId",
                table: "SyncRuleMapping",
                column: "SynchronisationRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
