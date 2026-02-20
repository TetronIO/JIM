using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveInitiatorNavigationProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_ApiKeys_InitiatedByApiKeyId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_Activities_MetaverseObjects_MetaverseObjectId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_ScheduleExecutions_ApiKeys_InitiatedByApiKeyId",
                table: "ScheduleExecutions");

            migrationBuilder.DropForeignKey(
                name: "FK_ScheduleExecutions_MetaverseObjects_InitiatedById",
                table: "ScheduleExecutions");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkerTasks_ApiKeys_InitiatedByApiKeyId",
                table: "WorkerTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkerTasks_MetaverseObjects_InitiatedByMetaverseObjectId",
                table: "WorkerTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkerTasks_InitiatedByApiKeyId",
                table: "WorkerTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkerTasks_InitiatedByMetaverseObjectId",
                table: "WorkerTasks");

            migrationBuilder.DropIndex(
                name: "IX_ScheduleExecutions_InitiatedByApiKeyId",
                table: "ScheduleExecutions");

            migrationBuilder.DropIndex(
                name: "IX_ScheduleExecutions_InitiatedById",
                table: "ScheduleExecutions");

            migrationBuilder.DropIndex(
                name: "IX_Activities_InitiatedByApiKeyId",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_MetaverseObjectId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "InitiatedByApiKeyId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "InitiatedByMetaverseObjectId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "InitiatedByApiKeyId",
                table: "ScheduleExecutions");

            migrationBuilder.DropColumn(
                name: "InitiatedByApiKeyId",
                table: "Activities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedByApiKeyId",
                table: "WorkerTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedByMetaverseObjectId",
                table: "WorkerTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedByApiKeyId",
                table: "ScheduleExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedByApiKeyId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_InitiatedByApiKeyId",
                table: "WorkerTasks",
                column: "InitiatedByApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_InitiatedByMetaverseObjectId",
                table: "WorkerTasks",
                column: "InitiatedByMetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_InitiatedByApiKeyId",
                table: "ScheduleExecutions",
                column: "InitiatedByApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleExecutions_InitiatedById",
                table: "ScheduleExecutions",
                column: "InitiatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_InitiatedByApiKeyId",
                table: "Activities",
                column: "InitiatedByApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_MetaverseObjectId",
                table: "Activities",
                column: "MetaverseObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_ApiKeys_InitiatedByApiKeyId",
                table: "Activities",
                column: "InitiatedByApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_MetaverseObjects_MetaverseObjectId",
                table: "Activities",
                column: "MetaverseObjectId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ScheduleExecutions_ApiKeys_InitiatedByApiKeyId",
                table: "ScheduleExecutions",
                column: "InitiatedByApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ScheduleExecutions_MetaverseObjects_InitiatedById",
                table: "ScheduleExecutions",
                column: "InitiatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkerTasks_ApiKeys_InitiatedByApiKeyId",
                table: "WorkerTasks",
                column: "InitiatedByApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkerTasks_MetaverseObjects_InitiatedByMetaverseObjectId",
                table: "WorkerTasks",
                column: "InitiatedByMetaverseObjectId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }
    }
}
