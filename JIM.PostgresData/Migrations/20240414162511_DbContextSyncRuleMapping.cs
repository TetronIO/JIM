using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextSyncRuleMapping : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_ConnectedSystemAttributes_TargetConnectedSy~",
                table: "SyncRuleMapping");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_MetaverseAttributes_TargetMetaverseAttribut~",
                table: "SyncRuleMapping");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_MetaverseObjects_CreatedById",
                table: "SyncRuleMapping");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_SyncRules_ObjectMatchingSynchronisationRule~",
                table: "SyncRuleMapping");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMapping_SyncRuleMappingId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleMapping",
                table: "SyncRuleMapping");

            migrationBuilder.RenameTable(
                name: "SyncRuleMapping",
                newName: "SyncRuleMappings");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMapping_TargetMetaverseAttributeId",
                table: "SyncRuleMappings",
                newName: "IX_SyncRuleMappings_TargetMetaverseAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMapping_TargetConnectedSystemAttributeId",
                table: "SyncRuleMappings",
                newName: "IX_SyncRuleMappings_TargetConnectedSystemAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMapping_ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMappings",
                newName: "IX_SyncRuleMappings_ObjectMatchingSynchronisationRuleId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMapping_CreatedById",
                table: "SyncRuleMappings",
                newName: "IX_SyncRuleMappings_CreatedById");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMapping_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMappings",
                newName: "IX_SyncRuleMappings_AttributeFlowSynchronisationRuleId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleMappings",
                table: "SyncRuleMappings",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_ConnectedSystemAttributes_TargetConnectedS~",
                table: "SyncRuleMappings",
                column: "TargetConnectedSystemAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_MetaverseAttributes_TargetMetaverseAttribu~",
                table: "SyncRuleMappings",
                column: "TargetMetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_MetaverseObjects_CreatedById",
                table: "SyncRuleMappings",
                column: "CreatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_AttributeFlowSynchronisationRule~",
                table: "SyncRuleMappings",
                column: "AttributeFlowSynchronisationRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_ObjectMatchingSynchronisationRul~",
                table: "SyncRuleMappings",
                column: "ObjectMatchingSynchronisationRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMappings_SyncRuleMappingId",
                table: "SyncRuleMappingSources",
                column: "SyncRuleMappingId",
                principalTable: "SyncRuleMappings",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_ConnectedSystemAttributes_TargetConnectedS~",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_MetaverseAttributes_TargetMetaverseAttribu~",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_MetaverseObjects_CreatedById",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_AttributeFlowSynchronisationRule~",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_ObjectMatchingSynchronisationRul~",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMappings_SyncRuleMappingId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleMappings",
                table: "SyncRuleMappings");

            migrationBuilder.RenameTable(
                name: "SyncRuleMappings",
                newName: "SyncRuleMapping");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappings_TargetMetaverseAttributeId",
                table: "SyncRuleMapping",
                newName: "IX_SyncRuleMapping_TargetMetaverseAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappings_TargetConnectedSystemAttributeId",
                table: "SyncRuleMapping",
                newName: "IX_SyncRuleMapping_TargetConnectedSystemAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappings_ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMapping",
                newName: "IX_SyncRuleMapping_ObjectMatchingSynchronisationRuleId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappings_CreatedById",
                table: "SyncRuleMapping",
                newName: "IX_SyncRuleMapping_CreatedById");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappings_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMapping",
                newName: "IX_SyncRuleMapping_AttributeFlowSynchronisationRuleId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleMapping",
                table: "SyncRuleMapping",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMapping_ConnectedSystemAttributes_TargetConnectedSy~",
                table: "SyncRuleMapping",
                column: "TargetConnectedSystemAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMapping_MetaverseAttributes_TargetMetaverseAttribut~",
                table: "SyncRuleMapping",
                column: "TargetMetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMapping_MetaverseObjects_CreatedById",
                table: "SyncRuleMapping",
                column: "CreatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");

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
                name: "FK_SyncRuleMappingSources_SyncRuleMapping_SyncRuleMappingId",
                table: "SyncRuleMappingSources",
                column: "SyncRuleMappingId",
                principalTable: "SyncRuleMapping",
                principalColumn: "Id");
        }
    }
}
