using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class AddDataGenMinMaxMvaRefControls : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MvaRefMaxAssignments",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MvaRefMinAssignments",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MvaRefMaxAssignments",
                table: "DataGenerationTemplateAttributes");

            migrationBuilder.DropColumn(
                name: "MvaRefMinAssignments",
                table: "DataGenerationTemplateAttributes");
        }
    }
}
