using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncRuleRefactor : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_FunctionParameter_FunctionP~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "SyncRuleMapping");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "FunctionParameter");

            migrationBuilder.AlterColumn<int>(
                name: "FunctionParameterId",
                table: "SyncRuleMappingSourceParamValue",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "BoolValue",
                table: "SyncRuleMappingSourceParamValue",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "IntValue",
                table: "SyncRuleMappingSourceParamValue",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "SyncRuleMappingSourceParamValue",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_FunctionParameter_FunctionP~",
                table: "SyncRuleMappingSourceParamValue",
                column: "FunctionParameterId",
                principalTable: "FunctionParameter",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_FunctionParameter_FunctionP~",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropColumn(
                name: "BoolValue",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropColumn(
                name: "IntValue",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "SyncRuleMappingSourceParamValue");

            migrationBuilder.AlterColumn<int>(
                name: "FunctionParameterId",
                table: "SyncRuleMappingSourceParamValue",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "SyncRuleMapping",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "FunctionParameter",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValue_FunctionParameter_FunctionP~",
                table: "SyncRuleMappingSourceParamValue",
                column: "FunctionParameterId",
                principalTable: "FunctionParameter",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
