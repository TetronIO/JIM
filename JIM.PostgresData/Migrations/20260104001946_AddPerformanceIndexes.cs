using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_Status_Timestamp",
                table: "WorkerTasks",
                columns: new[] { "Status", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectTypes_Name_DeletionRule",
                table: "MetaverseObjectTypes",
                columns: new[] { "Name", "DeletionRule" });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjects_Origin_Type_LastDisconnected",
                table: "MetaverseObjects",
                columns: new[] { "Origin", "TypeId", "LastConnectorDisconnectedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Created",
                table: "Activities",
                column: "Created",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_ConnectedSystemId",
                table: "SyncRules",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_DeferredReferences_SourceCsoId",
                table: "DeferredReferences",
                column: "SourceCsoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkerTasks_Status_Timestamp",
                table: "WorkerTasks");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectTypes_Name_DeletionRule",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjects_Origin_Type_LastDisconnected",
                table: "MetaverseObjects");

            migrationBuilder.DropIndex(
                name: "IX_Activities_Created",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_SyncRules_ConnectedSystemId",
                table: "SyncRules");

            migrationBuilder.DropIndex(
                name: "IX_DeferredReferences_SourceCsoId",
                table: "DeferredReferences");
        }
    }
}
