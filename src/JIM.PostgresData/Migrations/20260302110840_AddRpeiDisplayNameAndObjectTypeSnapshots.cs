using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRpeiDisplayNameAndObjectTypeSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayNameSnapshot",
                table: "ActivityRunProfileExecutionItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObjectTypeSnapshot",
                table: "ActivityRunProfileExecutionItems",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayNameSnapshot",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropColumn(
                name: "ObjectTypeSnapshot",
                table: "ActivityRunProfileExecutionItems");
        }
    }
}
