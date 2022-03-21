using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class MakePopulatedValuesPercentageNullable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "PopulatedValuesPercentage",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "ManagerDepthPercentage",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManagerDepthPercentage",
                table: "DataGenerationTemplateAttributes");

            migrationBuilder.AlterColumn<int>(
                name: "PopulatedValuesPercentage",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
