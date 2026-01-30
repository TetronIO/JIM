using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleStepTypedConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Configuration",
                table: "ScheduleSteps");

            migrationBuilder.AddColumn<string>(
                name: "Arguments",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemId",
                table: "ScheduleSteps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutablePath",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunProfileId",
                table: "ScheduleSteps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScriptPath",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SqlConnectionString",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SqlScriptPath",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkingDirectory",
                table: "ScheduleSteps",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Arguments",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemId",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "ExecutablePath",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "RunProfileId",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "ScriptPath",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "SqlConnectionString",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "SqlScriptPath",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "WorkingDirectory",
                table: "ScheduleSteps");

            migrationBuilder.AddColumn<string>(
                name: "Configuration",
                table: "ScheduleSteps",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
