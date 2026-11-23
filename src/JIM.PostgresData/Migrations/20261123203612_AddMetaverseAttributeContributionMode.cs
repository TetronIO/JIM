using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaverseAttributeContributionMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContributionMode",
                table: "MetaverseAttributes",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContributionMode",
                table: "MetaverseAttributes");
        }
    }
}
