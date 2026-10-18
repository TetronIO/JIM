using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// JIM never gives up on a Pending Export (#2119). Drops the retry cap and the backoff schedule, adds the time a
    /// failing export began failing, and moves every row out of the retired Failed statuses (3, on both tables) so it
    /// is attempted again at the next export run.
    /// </summary>
    public partial class PendingExportsNeverGiveUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FailingSince",
                table: "PendingExports",
                type: "timestamp with time zone",
                nullable: true);

            // An export made Failed by changes no import confirmed is waiting to send them again; one made Failed by
            // refused attempts is waiting to be attempted again, and is failing. Classified before the changes move.
            migrationBuilder.Sql("""
                UPDATE "PendingExports" pe SET "Status" = 1
                WHERE pe."Status" = 3
                  AND EXISTS (SELECT 1 FROM "PendingExportAttributeValueChanges" c
                              WHERE c."PendingExportId" = pe."Id" AND c."Status" = 3);

                UPDATE "PendingExportAttributeValueChanges" SET "Status" = 2 WHERE "Status" = 3;

                UPDATE "PendingExports" SET "Status" = 0 WHERE "Status" = 3;

                UPDATE "PendingExports" SET "FailingSince" = COALESCE("LastAttemptedAt", "CreatedAt")
                WHERE "Status" = 0 AND "ErrorCount" > 0;
                """);

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "PendingExports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailingSince",
                table: "PendingExports");

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "PendingExports",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "PendingExports",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
