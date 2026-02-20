using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ActivityInitiatorTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkerTasks_MetaverseObjects_InitiatedById",
                table: "WorkerTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkerTasks_InitiatedById",
                table: "WorkerTasks");

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

            migrationBuilder.AddColumn<int>(
                name: "InitiatedByType",
                table: "WorkerTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedByApiKeyId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedById",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InitiatedByType",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_InitiatedByApiKeyId",
                table: "WorkerTasks",
                column: "InitiatedByApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_InitiatedByMetaverseObjectId",
                table: "WorkerTasks",
                column: "InitiatedByMetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_InitiatedByApiKeyId",
                table: "Activities",
                column: "InitiatedByApiKeyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_ApiKeys_InitiatedByApiKeyId",
                table: "Activities",
                column: "InitiatedByApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_ApiKeys_InitiatedByApiKeyId",
                table: "Activities");

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
                name: "IX_Activities_InitiatedByApiKeyId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "InitiatedByApiKeyId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "InitiatedByMetaverseObjectId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "InitiatedByType",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "InitiatedByApiKeyId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "InitiatedById",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "InitiatedByType",
                table: "Activities");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_InitiatedById",
                table: "WorkerTasks",
                column: "InitiatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkerTasks_MetaverseObjects_InitiatedById",
                table: "WorkerTasks",
                column: "InitiatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }
    }
}
