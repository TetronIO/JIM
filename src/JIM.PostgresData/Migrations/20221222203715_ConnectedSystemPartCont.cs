using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectedSystemPartCont : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystemContainer_Connected~",
                table: "ConnectedSystemContainer");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystemPartition_Partition~",
                table: "ConnectedSystemContainer");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemContainer");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemPartition_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemPartition");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemPartition",
                table: "ConnectedSystemPartition");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemContainer",
                table: "ConnectedSystemContainer");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemPartition",
                newName: "ConnectedSystemPartitions");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemContainer",
                newName: "ConnectedSystemContainers");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemPartition_ConnectedSystemId",
                table: "ConnectedSystemPartitions",
                newName: "IX_ConnectedSystemPartitions_ConnectedSystemId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainer_PartitionId",
                table: "ConnectedSystemContainers",
                newName: "IX_ConnectedSystemContainers_PartitionId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainer_ConnectedSystemId",
                table: "ConnectedSystemContainers",
                newName: "IX_ConnectedSystemContainers_ConnectedSystemId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainer_ConnectedSystemContainerId",
                table: "ConnectedSystemContainers",
                newName: "IX_ConnectedSystemContainers_ConnectedSystemContainerId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemPartitions",
                table: "ConnectedSystemPartitions",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemContainers",
                table: "ConnectedSystemContainers",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_Connect~",
                table: "ConnectedSystemContainers",
                column: "ConnectedSystemContainerId",
                principalTable: "ConnectedSystemContainers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemContainers",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemContainers",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemPartitions_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemPartitions",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_Connect~",
                table: "ConnectedSystemContainers");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemContainers");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemContainers");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemPartitions_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemPartitions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemPartitions",
                table: "ConnectedSystemPartitions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemContainers",
                table: "ConnectedSystemContainers");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemPartitions",
                newName: "ConnectedSystemPartition");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemContainers",
                newName: "ConnectedSystemContainer");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemPartitions_ConnectedSystemId",
                table: "ConnectedSystemPartition",
                newName: "IX_ConnectedSystemPartition_ConnectedSystemId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainers_PartitionId",
                table: "ConnectedSystemContainer",
                newName: "IX_ConnectedSystemContainer_PartitionId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainers_ConnectedSystemId",
                table: "ConnectedSystemContainer",
                newName: "IX_ConnectedSystemContainer_ConnectedSystemId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemContainers_ConnectedSystemContainerId",
                table: "ConnectedSystemContainer",
                newName: "IX_ConnectedSystemContainer_ConnectedSystemContainerId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemPartition",
                table: "ConnectedSystemPartition",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemContainer",
                table: "ConnectedSystemContainer",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystemContainer_Connected~",
                table: "ConnectedSystemContainer",
                column: "ConnectedSystemContainerId",
                principalTable: "ConnectedSystemContainer",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystemPartition_Partition~",
                table: "ConnectedSystemContainer",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartition",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainer_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemContainer",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemPartition_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemPartition",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
