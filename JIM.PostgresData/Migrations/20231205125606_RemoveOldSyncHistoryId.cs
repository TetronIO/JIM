using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class RemoveOldSyncHistoryId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SyncRunHistoryDetailItemId",
                table: "ConnectedSystemObjectChanges",
                newName: "ActivityRunProfileExecutionItemId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemObjectChanges_SyncRunHistoryDetailItemId",
                table: "ConnectedSystemObjectChanges",
                newName: "IX_ConnectedSystemObjectChanges_ActivityRunProfileExecutionIte~");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ActivityRunProfileExecutionItemId",
                table: "ConnectedSystemObjectChanges",
                newName: "SyncRunHistoryDetailItemId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemObjectChanges_ActivityRunProfileExecutionIte~",
                table: "ConnectedSystemObjectChanges",
                newName: "IX_ConnectedSystemObjectChanges_SyncRunHistoryDetailItemId");
        }
    }
}
