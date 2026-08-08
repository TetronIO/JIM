using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AuxiliaryClassDiscoveryWorkerTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuxiliaryClassDiscoveryWorkerTask_ConnectedSystemId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SampleSizePerObjectType",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuxiliaryClassDiscoveryWorkerTask_ConnectedSystemId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "SampleSizePerObjectType",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "WorkerTasks");
        }
    }
}
