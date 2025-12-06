using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddDeleteConnectedSystemWorkerTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeleteConnectedSystemWorkerTask_ConnectedSystemId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EvaluateMvoDeletionRules",
                table: "WorkerTasks",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeleteConnectedSystemWorkerTask_ConnectedSystemId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "EvaluateMvoDeletionRules",
                table: "WorkerTasks");
        }
    }
}
