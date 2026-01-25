using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddDeleteChangeHistoryToWorkerTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClearConnectedSystemObjectsWorkerTask_DeleteChangeHistory",
                table: "WorkerTasks",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeleteChangeHistory",
                table: "WorkerTasks",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClearConnectedSystemObjectsWorkerTask_DeleteChangeHistory",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "DeleteChangeHistory",
                table: "WorkerTasks");
        }
    }
}
