using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddInitiatorFieldsToMetaverseObjectChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_MetaverseObjects_ChangeInitiatorId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChanges_ChangeInitiatorId",
                table: "MetaverseObjectChanges");

            migrationBuilder.RenameColumn(
                name: "ChangeInitiatorId",
                table: "MetaverseObjectChanges",
                newName: "InitiatedById");

            migrationBuilder.AddColumn<string>(
                name: "InitiatedByName",
                table: "MetaverseObjectChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InitiatedByType",
                table: "MetaverseObjectChanges",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatedByName",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropColumn(
                name: "InitiatedByType",
                table: "MetaverseObjectChanges");

            migrationBuilder.RenameColumn(
                name: "InitiatedById",
                table: "MetaverseObjectChanges",
                newName: "ChangeInitiatorId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_ChangeInitiatorId",
                table: "MetaverseObjectChanges",
                column: "ChangeInitiatorId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_MetaverseObjects_ChangeInitiatorId",
                table: "MetaverseObjectChanges",
                column: "ChangeInitiatorId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }
    }
}
