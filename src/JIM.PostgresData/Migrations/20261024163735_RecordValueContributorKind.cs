using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RecordValueContributorKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContributedByClaimType",
                table: "MetaverseObjectChangeAttributeValues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContributedByKind",
                table: "MetaverseObjectChangeAttributeValues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContributedByClaimType",
                table: "MetaverseObjectAttributeValues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContributedByKind",
                table: "MetaverseObjectAttributeValues",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContributedByClaimType",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "ContributedByKind",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropColumn(
                name: "ContributedByClaimType",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "ContributedByKind",
                table: "MetaverseObjectAttributeValues");
        }
    }
}
