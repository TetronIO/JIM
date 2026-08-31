using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSynchronisedDeprovisioningToDeleteConnectedSystemWorkerTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckpointConnectedSystemObjectId",
                table: "WorkerTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckpointPhase",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CheckpointSyncRuleId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SynchronisedDeprovisioning",
                table: "WorkerTasks",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointConnectedSystemObjectId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "CheckpointPhase",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "CheckpointSyncRuleId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "SynchronisedDeprovisioning",
                table: "WorkerTasks");
        }
    }
}
