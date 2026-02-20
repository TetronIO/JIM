using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectionLockedToAttribute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports");

            migrationBuilder.AddColumn<bool>(
                name: "SelectionLocked",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "SelectionLocked",
                table: "ConnectedSystemAttributes");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                table: "PendingExports",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }
    }
}
