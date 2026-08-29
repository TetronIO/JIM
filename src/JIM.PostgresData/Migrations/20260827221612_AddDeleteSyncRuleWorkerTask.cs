using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddDeleteSyncRuleWorkerTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RecallContributedValues",
                table: "WorkerTasks",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SyncRuleId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecallContributedValues",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "SyncRuleId",
                table: "WorkerTasks");
        }
    }
}
