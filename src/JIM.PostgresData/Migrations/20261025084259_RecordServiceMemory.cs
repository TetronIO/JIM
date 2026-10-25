using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RecordServiceMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MemoryAvailableBytes",
                table: "ServiceHeartbeats",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryTotalBytes",
                table: "ServiceHeartbeats",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProcessMemoryBytes",
                table: "ServiceHeartbeats",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MemoryAvailableBytes",
                table: "ServiceHeartbeats");

            migrationBuilder.DropColumn(
                name: "MemoryTotalBytes",
                table: "ServiceHeartbeats");

            migrationBuilder.DropColumn(
                name: "ProcessMemoryBytes",
                table: "ServiceHeartbeats");
        }
    }
}
