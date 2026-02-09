using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Interval",
                table: "Schedules");

            migrationBuilder.RenameColumn(
                name: "Modified",
                table: "Schedules",
                newName: "LastUpdated");

            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "ScheduleSteps",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ScheduleSteps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ScheduleSteps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "ScheduleSteps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ScheduleSteps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ScheduleSteps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "Schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "Schedules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "Schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "Schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "Schedules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "Schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Created",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "Schedules");

            migrationBuilder.RenameColumn(
                name: "LastUpdated",
                table: "Schedules",
                newName: "Modified");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "Interval",
                table: "Schedules",
                type: "interval",
                nullable: true);
        }
    }
}
