using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextSyncRuleParamValues : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_ConnectedSystemAttributes_C~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_FunctionParameter_FunctionP~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_MetaverseAttributes_Metaver~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_SyncRuleMappingSources_Sync~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleMappingSourceParamValue",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.RenameTable(
                name: "SyncRuleMappingSourceParamValue",
                newName: "SyncRuleMappingSourceParamValues");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValue_SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValues",
                newName: "IX_SyncRuleMappingSourceParamValues_SyncRuleMappingSourceId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValue_MetaverseAttributeId",
                table: "SyncRuleMappingSourceParamValues",
                newName: "IX_SyncRuleMappingSourceParamValues_MetaverseAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValue_FunctionParameterId",
                table: "SyncRuleMappingSourceParamValues",
                newName: "IX_SyncRuleMappingSourceParamValues_FunctionParameterId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValue_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSourceParamValues",
                newName: "IX_SyncRuleMappingSourceParamValues_ConnectedSystemAttributeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleMappingSourceParamValues",
                table: "SyncRuleMappingSourceParamValues",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_ConnectedSystemAttributes_~",
                table: "SyncRuleMappingSourceParamValues",
                column: "ConnectedSystemAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_FunctionParameter_Function~",
                table: "SyncRuleMappingSourceParamValues",
                column: "FunctionParameterId",
                principalTable: "FunctionParameter",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_MetaverseAttributes_Metave~",
                table: "SyncRuleMappingSourceParamValues",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_SyncRuleMappingSources_Syn~",
                table: "SyncRuleMappingSourceParamValues",
                column: "SyncRuleMappingSourceId",
                principalTable: "SyncRuleMappingSources",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_ConnectedSystemAttributes_~",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_FunctionParameter_Function~",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_MetaverseAttributes_Metave~",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_SyncRuleMappingSources_Syn~",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleMappingSourceParamValues",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.RenameTable(
                name: "SyncRuleMappingSourceParamValues",
                newName: "SyncRuleMappingSourceParamValue");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValues_SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValue",
                newName: "IX_SyncRuleMappingSourceParamValue_SyncRuleMappingSourceId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValues_MetaverseAttributeId",
                table: "SyncRuleMappingSourceParamValue",
                newName: "IX_SyncRuleMappingSourceParamValue_MetaverseAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValues_FunctionParameterId",
                table: "SyncRuleMappingSourceParamValue",
                newName: "IX_SyncRuleMappingSourceParamValue_FunctionParameterId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleMappingSourceParamValues_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSourceParamValue",
                newName: "IX_SyncRuleMappingSourceParamValue_ConnectedSystemAttributeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleMappingSourceParamValue",
                table: "SyncRuleMappingSourceParamValue",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_ConnectedSystemAttributes_C~",
                table: "SyncRuleMappingSourceParamValue",
                column: "ConnectedSystemAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_FunctionParameter_FunctionP~",
                table: "SyncRuleMappingSourceParamValue",
                column: "FunctionParameterId",
                principalTable: "FunctionParameter",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_MetaverseAttributes_Metaver~",
                table: "SyncRuleMappingSourceParamValue",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_SyncRuleMappingSources_Sync~",
                table: "SyncRuleMappingSourceParamValue",
                column: "SyncRuleMappingSourceId",
                principalTable: "SyncRuleMappingSources",
                principalColumn: "Id");
        }
    }
}
