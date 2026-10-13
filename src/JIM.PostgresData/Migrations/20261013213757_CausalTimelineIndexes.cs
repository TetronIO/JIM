using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CausalTimelineIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_DeletedMetaverseObjectId",
                table: "MetaverseObjectChanges",
                column: "DeletedMetaverseObjectId",
                filter: "\"DeletedMetaverseObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedConnectedSystemObjectId",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedConnectedSystemObjectId",
                filter: "\"DeletedConnectedSystemObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItems_MetaverseObjectId",
                table: "ActivityRunProfileExecutionItems",
                column: "MetaverseObjectId",
                filter: "\"MetaverseObjectId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChanges_DeletedMetaverseObjectId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedConnectedSystemObjectId",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_ActivityRunProfileExecutionItems_MetaverseObjectId",
                table: "ActivityRunProfileExecutionItems");
        }
    }
}
