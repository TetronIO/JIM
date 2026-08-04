using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ActivityScheduleAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduledByScheduleId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduledByScheduleName",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ScheduledByScheduleId",
                table: "Activities",
                column: "ScheduledByScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ScheduleExecutionId",
                table: "Activities",
                column: "ScheduleExecutionId");

            // Backfill the denormalised Schedule attribution for existing Activities from the Schedule Executions
            // that are still present. Idempotent: the IS NULL guard means a re-run touches no already-populated row,
            // and Activities whose execution has since been pruned or cascaded away simply stay unattributed.
            migrationBuilder.Sql("""
                UPDATE "Activities" a
                SET "ScheduledByScheduleId" = se."ScheduleId",
                    "ScheduledByScheduleName" = se."ScheduleName"
                FROM "ScheduleExecutions" se
                WHERE a."ScheduleExecutionId" = se."Id"
                  AND a."ScheduledByScheduleId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_ScheduledByScheduleId",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ScheduleExecutionId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ScheduledByScheduleId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ScheduledByScheduleName",
                table: "Activities");
        }
    }
}
