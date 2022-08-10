using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class PredefinedSearchUri : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Uri",
                table: "PredefinedSearches",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearches_Uri",
                table: "PredefinedSearches",
                column: "Uri");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PredefinedSearches_Uri",
                table: "PredefinedSearches");

            migrationBuilder.DropColumn(
                name: "Uri",
                table: "PredefinedSearches");
        }
    }
}
