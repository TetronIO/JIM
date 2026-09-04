using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRunProfileExportLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxCreates",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxDeletes",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxUpdates",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExportCreatesWithheld",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExportDeletesWithheld",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExportUpdatesWithheld",
                table: "Activities",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxCreates",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "MaxDeletes",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "MaxUpdates",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "ExportCreatesWithheld",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ExportDeletesWithheld",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ExportUpdatesWithheld",
                table: "Activities");
        }
    }
}
