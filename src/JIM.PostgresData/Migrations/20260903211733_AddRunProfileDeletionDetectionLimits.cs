using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRunProfileDeletionDetectionLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxDetectedDeletions",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxDetectedDeletionsPercent",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DetectedDeletionsWithheld",
                table: "Activities",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxDetectedDeletions",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "MaxDetectedDeletionsPercent",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "DetectedDeletionsWithheld",
                table: "Activities");
        }
    }
}
