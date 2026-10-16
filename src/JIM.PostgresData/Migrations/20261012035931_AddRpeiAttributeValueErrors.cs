using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRpeiAttributeValueErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributeValueErrorsJson",
                table: "ActivityRunProfileExecutionItems",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLocation",
                table: "ActivityRunProfileExecutionItems",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttributeValueErrorsJson",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropColumn(
                name: "SourceLocation",
                table: "ActivityRunProfileExecutionItems");
        }
    }
}
