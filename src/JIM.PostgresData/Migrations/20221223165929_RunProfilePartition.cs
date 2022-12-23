using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class RunProfilePartition : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartitionId",
                table: "ConnectedSystemRunProfile",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemRunProfile_PartitionId",
                table: "ConnectedSystemRunProfile",
                column: "PartitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemRunProfile_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemRunProfile",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemRunProfile_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemRunProfile");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemRunProfile_PartitionId",
                table: "ConnectedSystemRunProfile");

            migrationBuilder.DropColumn(
                name: "PartitionId",
                table: "ConnectedSystemRunProfile");
        }
    }
}
