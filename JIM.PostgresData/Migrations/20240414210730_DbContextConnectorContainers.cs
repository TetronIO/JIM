using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextConnectorContainers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectorPartition",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorPartition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorContainers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectorPartitionId = table.Column<string>(type: "text", nullable: true),
                    ConnectorContainerId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorContainers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectorContainers_ConnectorContainers_ConnectorContainerId",
                        column: x => x.ConnectorContainerId,
                        principalTable: "ConnectorContainers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectorContainers_ConnectorPartition_ConnectorPartitionId",
                        column: x => x.ConnectorPartitionId,
                        principalTable: "ConnectorPartition",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorContainers_ConnectorContainerId",
                table: "ConnectorContainers",
                column: "ConnectorContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorContainers_ConnectorPartitionId",
                table: "ConnectorContainers",
                column: "ConnectorPartitionId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectorContainers");

            migrationBuilder.DropTable(
                name: "ConnectorPartition");
        }
    }
}
