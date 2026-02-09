using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleExecutionId",
                table: "WorkerTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleStepIndex",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    TriggerType = table.Column<int>(type: "integer", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: true),
                    Interval = table.Column<TimeSpan>(type: "interval", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentStepIndex = table.Column<int>(type: "integer", nullable: false),
                    TotalSteps = table.Column<int>(type: "integer", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InitiatedByType = table.Column<int>(type: "integer", nullable: false),
                    InitiatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatedByName = table.Column<string>(type: "text", nullable: true),
                    InitiatedByApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleExecutions_ApiKeys_InitiatedByApiKeyId",
                        column: x => x.InitiatedByApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ScheduleExecutions_MetaverseObjects_InitiatedById",
                        column: x => x.InitiatedById,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ScheduleExecutions_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ExecutionMode = table.Column<int>(type: "integer", nullable: false),
                    StepType = table.Column<int>(type: "integer", nullable: false),
                    Configuration = table.Column<string>(type: "text", nullable: false),
                    ContinueOnFailure = table.Column<bool>(type: "boolean", nullable: false),
                    Timeout = table.Column<TimeSpan>(type: "interval", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleSteps_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_ScheduleExecutionId",
                table: "WorkerTasks",
                column: "ScheduleExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_InitiatedByApiKeyId",
                table: "ScheduleExecutions",
                column: "InitiatedByApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_InitiatedById",
                table: "ScheduleExecutions",
                column: "InitiatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_ScheduleId",
                table: "ScheduleExecutions",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_Status_QueuedAt",
                table: "ScheduleExecutions",
                columns: new[] { "Status", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_IsEnabled_NextRunTime",
                table: "Schedules",
                columns: new[] { "IsEnabled", "NextRunTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_Name",
                table: "Schedules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleSteps_ScheduleId_StepIndex",
                table: "ScheduleSteps",
                columns: new[] { "ScheduleId", "StepIndex" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkerTasks_ScheduleExecutions_ScheduleExecutionId",
                table: "WorkerTasks",
                column: "ScheduleExecutionId",
                principalTable: "ScheduleExecutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkerTasks_ScheduleExecutions_ScheduleExecutionId",
                table: "WorkerTasks");

            migrationBuilder.DropTable(
                name: "ScheduleExecutions");

            migrationBuilder.DropTable(
                name: "ScheduleSteps");

            migrationBuilder.DropTable(
                name: "Schedules");

            migrationBuilder.DropIndex(
                name: "IX_WorkerTasks_ScheduleExecutionId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "ScheduleExecutionId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "ScheduleStepIndex",
                table: "WorkerTasks");
        }
    }
}
