using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RenameLastDeltaSyncCompletedAtToLastSyncCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastDeltaSyncCompletedAt",
                table: "ConnectedSystems",
                newName: "LastSyncCompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastSyncCompletedAt",
                table: "ConnectedSystems",
                newName: "LastDeltaSyncCompletedAt");
        }
    }
}
