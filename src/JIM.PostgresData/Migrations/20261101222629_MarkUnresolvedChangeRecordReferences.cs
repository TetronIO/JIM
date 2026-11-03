using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class MarkUnresolvedChangeRecordReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUnresolvedReference",
                table: "ConnectedSystemObjectChangeAttributeValues",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChangeAttributeValues_Unresolved",
                table: "ConnectedSystemObjectChangeAttributeValues",
                column: "ConnectedSystemObjectChangeAttributeId",
                filter: "\"IsUnresolvedReference\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectChangeAttributeValues_Unresolved",
                table: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "IsUnresolvedReference",
                table: "ConnectedSystemObjectChangeAttributeValues");
        }
    }
}
