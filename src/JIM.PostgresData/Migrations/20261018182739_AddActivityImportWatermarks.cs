using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityImportWatermarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportWatermarkFrom",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportWatermarkTo",
                table: "Activities",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportWatermarkFrom",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ImportWatermarkTo",
                table: "Activities");
        }
    }
}
