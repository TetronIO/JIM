using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceStrandedValueSweepPendingWithArmedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StrandedValueSweepArmedAt",
                table: "ConnectedSystems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSuccessfulFullImportCompletedAt",
                table: "ConnectedSystems",
                type: "timestamp with time zone",
                nullable: true);

            // Carry every already-armed system's arming forward as a timestamp (#1605), rather than losing
            // it outright: the #1549 migration backfilled StrandedValueSweepPending = TRUE for every system
            // that existed at the time (a one-off self-heal for pre-feature strays), and any clear performed
            // since then set it true too. Stamping the migration's own time as the armed-at preserves exactly
            // the same "still waiting for a sweep" state those rows already carried; LastSuccessfulFullImportCompletedAt
            // starts null for every row, so the new #1605 gate keeps every one of them waiting for a genuine
            // Full Import before their next Full Synchronisation sweeps anything, which is what makes the old
            // flag-only behaviour (sweep on the very next Full Synchronisation, import or not) safe to retire.
            migrationBuilder.Sql(
                "UPDATE \"ConnectedSystems\" SET \"StrandedValueSweepArmedAt\" = NOW() WHERE \"StrandedValueSweepPending\" = TRUE;");

            migrationBuilder.DropColumn(
                name: "StrandedValueSweepPending",
                table: "ConnectedSystems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StrandedValueSweepPending",
                table: "ConnectedSystems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE \"ConnectedSystems\" SET \"StrandedValueSweepPending\" = TRUE WHERE \"StrandedValueSweepArmedAt\" IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "StrandedValueSweepArmedAt",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "LastSuccessfulFullImportCompletedAt",
                table: "ConnectedSystems");
        }
    }
}
