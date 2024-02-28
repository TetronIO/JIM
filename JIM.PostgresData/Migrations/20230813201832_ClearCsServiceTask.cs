using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ClearCsServiceTask : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SynchronisationServiceTask_ConnectedSystemId",
                table: "ServiceTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemId",
                table: "HistoryItems",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SynchronisationServiceTask_ConnectedSystemId",
                table: "ServiceTasks");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemId",
                table: "HistoryItems");
        }
    }
}
