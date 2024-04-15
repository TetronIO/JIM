using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextConnectorPartition : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorContainers_ConnectorPartition_ConnectorPartitionId",
                table: "ConnectorContainers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectorPartition",
                table: "ConnectorPartition");

            migrationBuilder.RenameTable(
                name: "ConnectorPartition",
                newName: "ConnectorPartitions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectorPartitions",
                table: "ConnectorPartitions",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorContainers_ConnectorPartitions_ConnectorPartitionId",
                table: "ConnectorContainers",
                column: "ConnectorPartitionId",
                principalTable: "ConnectorPartitions",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorContainers_ConnectorPartitions_ConnectorPartitionId",
                table: "ConnectorContainers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectorPartitions",
                table: "ConnectorPartitions");

            migrationBuilder.RenameTable(
                name: "ConnectorPartitions",
                newName: "ConnectorPartition");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectorPartition",
                table: "ConnectorPartition",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorContainers_ConnectorPartition_ConnectorPartitionId",
                table: "ConnectorContainers",
                column: "ConnectorPartitionId",
                principalTable: "ConnectorPartition",
                principalColumn: "Id");
        }
    }
}
