using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class UnresolvedReferences2 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UnresolvedReference",
                table: "ConnectedSystemObjectAttributeValues",
                newName: "UnresolvedReferenceValue");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UnresolvedReferenceValue",
                table: "ConnectedSystemObjectAttributeValues",
                newName: "UnresolvedReference");
        }
    }
}
