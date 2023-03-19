using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SomethingSelected : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Selected",
                table: "ConnectedSystemPartitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Hidden",
                table: "ConnectedSystemContainers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Selected",
                table: "ConnectedSystemContainers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Selected",
                table: "ConnectedSystemPartitions");

            migrationBuilder.DropColumn(
                name: "Hidden",
                table: "ConnectedSystemContainers");

            migrationBuilder.DropColumn(
                name: "Selected",
                table: "ConnectedSystemContainers");
        }
    }
}
