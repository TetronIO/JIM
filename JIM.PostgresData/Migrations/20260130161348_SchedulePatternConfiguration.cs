using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class SchedulePatternConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DaysOfWeek",
                table: "Schedules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntervalUnit",
                table: "Schedules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntervalValue",
                table: "Schedules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntervalWindowEnd",
                table: "Schedules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntervalWindowStart",
                table: "Schedules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PatternType",
                table: "Schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RunTimes",
                table: "Schedules",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DaysOfWeek",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "IntervalUnit",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "IntervalValue",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "IntervalWindowEnd",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "IntervalWindowStart",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "PatternType",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "RunTimes",
                table: "Schedules");
        }
    }
}
