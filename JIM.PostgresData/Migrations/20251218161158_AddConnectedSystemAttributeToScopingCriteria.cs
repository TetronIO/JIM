using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemAttributeToScopingCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_MetaverseAttributes_MetaverseAttrib~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.AlterColumn<int>(
                name: "MetaverseAttributeId",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemAttributeId",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteria_ConnectedSystemAttributeId",
                table: "SyncRuleScopingCriteria",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_ConnectedSystemAttributes_Connected~",
                table: "SyncRuleScopingCriteria",
                column: "ConnectedSystemAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_MetaverseAttributes_MetaverseAttrib~",
                table: "SyncRuleScopingCriteria",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_ConnectedSystemAttributes_Connected~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_MetaverseAttributes_MetaverseAttrib~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleScopingCriteria_ConnectedSystemAttributeId",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemAttributeId",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.AlterColumn<int>(
                name: "MetaverseAttributeId",
                table: "SyncRuleScopingCriteria",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_MetaverseAttributes_MetaverseAttrib~",
                table: "SyncRuleScopingCriteria",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
