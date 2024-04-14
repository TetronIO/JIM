using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextSyncRuleMappingSource : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSource_ConnectedSystemAttributes_ConnectedSy~",
                table: "SyncRuleMappingSource");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSource_Function_FunctionId",
                table: "SyncRuleMappingSource");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSource_MetaverseAttributes_MetaverseAttribut~",
                table: "SyncRuleMappingSource");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSource_SyncRuleMapping_SyncRuleMappingId",
                table: "SyncRuleMappingSource");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_SyncRuleMappingSource_SyncR~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleMappingSource",
                table: "SyncRuleMappingSource");

            migrationBuilder.RenameTable(
                name: "SyncRuleMappingSource",
                newName: "SyncRuleMappingSources");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSource_SyncRuleMappingId",
                table: "SyncRuleMappingSources",
                newName: "IX_SyncRuleMappingSources_SyncRuleMappingId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSource_MetaverseAttributeId",
                table: "SyncRuleMappingSources",
                newName: "IX_SyncRuleMappingSources_MetaverseAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSource_FunctionId",
                table: "SyncRuleMappingSources",
                newName: "IX_SyncRuleMappingSources_FunctionId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSource_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSources",
                newName: "IX_SyncRuleMappingSources_ConnectedSystemAttributeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleMappingSources",
                table: "SyncRuleMappingSources",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_SyncRuleMappingSources_Sync~",
                table: "SyncRuleMappingSourceParamValue",
                column: "SyncRuleMappingSourceId",
                principalTable: "SyncRuleMappingSources",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_ConnectedSystemAttributes_ConnectedS~",
                table: "SyncRuleMappingSources",
                column: "ConnectedSystemAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_Function_FunctionId",
                table: "SyncRuleMappingSources",
                column: "FunctionId",
                principalTable: "Function",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_MetaverseAttributes_MetaverseAttribu~",
                table: "SyncRuleMappingSources",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMapping_SyncRuleMappingId",
                table: "SyncRuleMappingSources",
                column: "SyncRuleMappingId",
                principalTable: "SyncRuleMapping",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_SyncRuleMappingSources_Sync~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_ConnectedSystemAttributes_ConnectedS~",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_Function_FunctionId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_MetaverseAttributes_MetaverseAttribu~",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMapping_SyncRuleMappingId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleMappingSources",
                table: "SyncRuleMappingSources");

            migrationBuilder.RenameTable(
                name: "SyncRuleMappingSources",
                newName: "SyncRuleMappingSource");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSources_SyncRuleMappingId",
                table: "SyncRuleMappingSource",
                newName: "IX_SyncRuleMappingSource_SyncRuleMappingId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSources_MetaverseAttributeId",
                table: "SyncRuleMappingSource",
                newName: "IX_SyncRuleMappingSource_MetaverseAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSources_FunctionId",
                table: "SyncRuleMappingSource",
                newName: "IX_SyncRuleMappingSource_FunctionId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSources_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSource",
                newName: "IX_SyncRuleMappingSource_ConnectedSystemAttributeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleMappingSource",
                table: "SyncRuleMappingSource",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSource_ConnectedSystemAttributes_ConnectedSy~",
                table: "SyncRuleMappingSource",
                column: "ConnectedSystemAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSource_Function_FunctionId",
                table: "SyncRuleMappingSource",
                column: "FunctionId",
                principalTable: "Function",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSource_MetaverseAttributes_MetaverseAttribut~",
                table: "SyncRuleMappingSource",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSource_SyncRuleMapping_SyncRuleMappingId",
                table: "SyncRuleMappingSource",
                column: "SyncRuleMappingId",
                principalTable: "SyncRuleMapping",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_SyncRuleMappingSource_SyncR~",
                table: "SyncRuleMappingSourceParamValue",
                column: "SyncRuleMappingSourceId",
                principalTable: "SyncRuleMappingSource",
                principalColumn: "Id");
        }
    }
}
