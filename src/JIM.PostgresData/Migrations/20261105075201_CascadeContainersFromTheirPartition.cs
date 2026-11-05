using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CascadeContainersFromTheirPartition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemContainers");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemContainers",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemContainers");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemContainers",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id");
        }
    }
}
