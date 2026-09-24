using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleFailureHandling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schedule failure handling (#1787). Hand-ordered so the old per-step boolean is mapped onto the new three-way
            // setting before it is dropped: every existing Schedule stops when a step fails (0 = Stop), a step whose
            // Continue on failure was on continues the Schedule (2 = Continue), and one whose setting was off follows the
            // Schedule (0 = FollowSchedule), which stops. Every run behaves exactly as it did before the upgrade.
            migrationBuilder.AddColumn<int>(
                name: "OnStepFailure",
                table: "Schedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OnFailure",
                table: "ScheduleSteps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE \"ScheduleSteps\" SET \"OnFailure\" = CASE WHEN \"ContinueOnFailure\" THEN 2 ELSE 0 END;");

            migrationBuilder.DropColumn(
                name: "ContinueOnFailure",
                table: "ScheduleSteps");

            // Copied from the step at queue time and never read; failure behaviour is read from the Schedule at the
            // moment of each decision.
            migrationBuilder.DropColumn(
                name: "ContinueOnFailure",
                table: "WorkerTasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContinueOnFailure",
                table: "WorkerTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ContinueOnFailure",
                table: "ScheduleSteps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Only a step's own Continue setting survives the downgrade; the Schedule-level setting has no equivalent in
            // the older schema, so a step that followed a continuing Schedule goes back to stopping.
            migrationBuilder.Sql("UPDATE \"ScheduleSteps\" SET \"ContinueOnFailure\" = (\"OnFailure\" = 2);");

            migrationBuilder.DropColumn(
                name: "OnFailure",
                table: "ScheduleSteps");

            migrationBuilder.DropColumn(
                name: "OnStepFailure",
                table: "Schedules");
        }
    }
}
