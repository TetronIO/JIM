using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleExecutionScheduleIdQueuedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScheduleExecutions_ScheduleId",
                table: "ScheduleExecutions");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_ScheduleId_QueuedAt",
                table: "ScheduleExecutions",
                columns: new[] { "ScheduleId", "QueuedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScheduleExecutions_ScheduleId_QueuedAt",
                table: "ScheduleExecutions");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_ScheduleId",
                table: "ScheduleExecutions",
                column: "ScheduleId");
        }
    }
}
