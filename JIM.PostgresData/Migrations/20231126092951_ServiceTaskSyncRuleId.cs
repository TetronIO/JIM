using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ServiceTaskSyncRuleId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SynchronisationRuleId",
                table: "Activities",
                newName: "SyncRuleId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SyncRuleId",
                table: "Activities",
                newName: "SynchronisationRuleId");
        }
    }
}
