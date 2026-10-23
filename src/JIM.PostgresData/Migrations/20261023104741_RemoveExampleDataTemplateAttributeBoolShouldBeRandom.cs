using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExampleDataTemplateAttributeBoolShouldBeRandom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoolShouldBeRandom",
                table: "ExampleDataTemplateAttributes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BoolShouldBeRandom",
                table: "ExampleDataTemplateAttributes",
                type: "boolean",
                nullable: true);
        }
    }
}
