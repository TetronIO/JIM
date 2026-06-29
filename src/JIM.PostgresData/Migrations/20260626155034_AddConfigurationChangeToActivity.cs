using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationChangeToActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangeReason",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfigurationChangeSnapshot",
                table: "Activities",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConfigurationChangeVersion",
                table: "Activities",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangeReason",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ConfigurationChangeSnapshot",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ConfigurationChangeVersion",
                table: "Activities");
        }
    }
}
