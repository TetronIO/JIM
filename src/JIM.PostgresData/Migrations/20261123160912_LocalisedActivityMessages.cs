using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class LocalisedActivityMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorMessageDetailJson",
                table: "ActivityRunProfileExecutionItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessageDetailJson",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageDetailJson",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WarningMessageDetailJson",
                table: "Activities",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorMessageDetailJson",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropColumn(
                name: "ErrorMessageDetailJson",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "MessageDetailJson",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "WarningMessageDetailJson",
                table: "Activities");
        }
    }
}
