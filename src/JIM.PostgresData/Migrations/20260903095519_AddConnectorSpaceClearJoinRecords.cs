using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorSpaceClearJoinRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClearedJoinRecordCount",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConnectorSpaceClearJoinRecords",
                columns: table => new
                {
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClearedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorSpaceClearJoinRecords", x => new { x.ConnectedSystemId, x.MetaverseObjectId });
                    table.ForeignKey(
                        name: "FK_ConnectorSpaceClearJoinRecords_ConnectedSystems_ConnectedSy~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorSpaceClearJoinRecords_ConnectedSystemId",
                table: "ConnectorSpaceClearJoinRecords",
                column: "ConnectedSystemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectorSpaceClearJoinRecords");

            migrationBuilder.DropColumn(
                name: "ClearedJoinRecordCount",
                table: "Activities");
        }
    }
}
