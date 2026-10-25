using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ConnectedSystemObjectTypeKeysetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_TypeId",
                table: "ConnectedSystemObjects");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_TypeId_Id",
                table: "ConnectedSystemObjects",
                columns: new[] { "ConnectedSystemId", "TypeId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_TypeId_Id",
                table: "ConnectedSystemObjects");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_TypeId",
                table: "ConnectedSystemObjects",
                columns: new[] { "ConnectedSystemId", "TypeId" });
        }
    }
}
