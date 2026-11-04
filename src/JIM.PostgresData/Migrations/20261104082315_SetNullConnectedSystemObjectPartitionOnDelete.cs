using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class SetNullConnectedSystemObjectPartitionOnDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId",
                table: "ConnectedSystemObjects");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId",
                table: "ConnectedSystemObjects",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId",
                table: "ConnectedSystemObjects");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjects_ConnectedSystemPartitions_PartitionId",
                table: "ConnectedSystemObjects",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id");
        }
    }
}
