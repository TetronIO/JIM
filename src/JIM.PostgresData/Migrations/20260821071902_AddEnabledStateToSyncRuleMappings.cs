using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddEnabledStateToSyncRuleMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisabledReason",
                table: "SyncRuleMappings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "SyncRuleMappings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledReason",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "SyncRuleMappings");
        }
    }
}
