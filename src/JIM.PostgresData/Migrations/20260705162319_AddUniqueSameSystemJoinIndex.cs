using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueSameSystemJoinIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique",
                table: "ConnectedSystemObjects",
                columns: new[] { "ConnectedSystemId", "MetaverseObjectId" },
                unique: true,
                filter: "\"MetaverseObjectId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique",
                table: "ConnectedSystemObjects");
        }
    }
}
