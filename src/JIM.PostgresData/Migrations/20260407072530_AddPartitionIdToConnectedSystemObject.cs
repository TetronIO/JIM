using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPartitionIdToConnectedSystemObject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartitionId",
                table: "ConnectedSystemObjects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_PartitionId",
                table: "ConnectedSystemObjects",
                column: "PartitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId",
                table: "ConnectedSystemObjects",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_PartitionId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "PartitionId",
                table: "ConnectedSystemObjects");
        }
    }
}
