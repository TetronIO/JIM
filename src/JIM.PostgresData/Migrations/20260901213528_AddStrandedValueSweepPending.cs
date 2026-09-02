using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddStrandedValueSweepPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StrandedValueSweepPending",
                table: "ConnectedSystems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // One-off upgrade self-heal (#1549): a deployment upgrading onto this migration may already
            // carry strays from Connector Space clears that predate the sweep feature entirely, with no
            // record of which systems were ever cleared. Arming every existing system's flag gives each one
            // exactly one sweep at its next Full Synchronisation; a system with nothing stranded pays only
            // the sweep's cost of finding zero candidates. New systems created after this migration default
            // to false via the column default above and only arm on an actual clear.
            migrationBuilder.Sql("UPDATE \"ConnectedSystems\" SET \"StrandedValueSweepPending\" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StrandedValueSweepPending",
                table: "ConnectedSystems");
        }
    }
}
