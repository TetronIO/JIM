using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRunProfileEnabledState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisabledPartitionName",
                table: "ConnectedSystemRunProfiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DisabledReason",
                table: "ConnectedSystemRunProfiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "ConnectedSystemRunProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledPartitionName",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "DisabledReason",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "ConnectedSystemRunProfiles");
        }
    }
}
