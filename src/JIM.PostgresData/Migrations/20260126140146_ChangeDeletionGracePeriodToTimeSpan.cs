using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ChangeDeletionGracePeriodToTimeSpan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add new interval column
            migrationBuilder.AddColumn<TimeSpan>(
                name: "DeletionGracePeriod",
                table: "MetaverseObjectTypes",
                type: "interval",
                nullable: true);

            // Migrate existing data: convert days (integer) to interval
            migrationBuilder.Sql(
                """
                UPDATE "MetaverseObjectTypes"
                SET "DeletionGracePeriod" = "DeletionGracePeriodDays" * INTERVAL '1 day'
                WHERE "DeletionGracePeriodDays" IS NOT NULL AND "DeletionGracePeriodDays" > 0
                """);

            // Drop old column
            migrationBuilder.DropColumn(
                name: "DeletionGracePeriodDays",
                table: "MetaverseObjectTypes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletionGracePeriod",
                table: "MetaverseObjectTypes");

            migrationBuilder.AddColumn<int>(
                name: "DeletionGracePeriodDays",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: true);
        }
    }
}
