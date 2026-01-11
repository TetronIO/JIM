using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class FixCsoChangeRefValueDeleteBehaviour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystem~1",
                table: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystem~1",
                table: "ConnectedSystemObjectChangeAttributeValues",
                column: "ReferenceValueId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystem~1",
                table: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystem~1",
                table: "ConnectedSystemObjectChangeAttributeValues",
                column: "ReferenceValueId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }
    }
}
