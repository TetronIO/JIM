using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class UnresolvedReferences : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UnresolvedReference",
                table: "ConnectedSystemObjectAttributeValues",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnresolvedReference",
                table: "ConnectedSystemObjectAttributeValues");
        }
    }
}
