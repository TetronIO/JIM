using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaRefreshRemovalWorkerTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<int>>(
                name: "RemovedAttributeIds",
                table: "WorkerTasks",
                type: "integer[]",
                nullable: true);

            migrationBuilder.AddColumn<List<int>>(
                name: "RemovedObjectTypeIds",
                table: "WorkerTasks",
                type: "integer[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchemaRefreshRemovalWorkerTask_ConnectedSystemId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemovedAttributeIds",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "RemovedObjectTypeIds",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "SchemaRefreshRemovalWorkerTask_ConnectedSystemId",
                table: "WorkerTasks");
        }
    }
}
