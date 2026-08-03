using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class PreviewRequestedDeltaPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 is Capped, not the enum's zero value (Full). Every preview that already exists was evaluated under
            // the unconditional cap, so backfilling them as Full would misdescribe what was actually kept, and would
            // silently promote any preview interrupted by a restart to a full evaluation when it is retried.
            migrationBuilder.AddColumn<int>(
                name: "RequestedDeltaPersistence",
                table: "ConfigurationChangePreviews",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedDeltaPersistence",
                table: "ConfigurationChangePreviews");
        }
    }
}
