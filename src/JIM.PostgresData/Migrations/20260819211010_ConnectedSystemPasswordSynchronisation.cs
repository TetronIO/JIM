using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ConnectedSystemPasswordSynchronisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectedSystemPasswordSynchronisations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    TargetObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    RetryBackoffBase = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RequireSecureTransport = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemPasswordSynchronisations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemPasswordSynchronisations_ConnectedSystemObje~",
                        column: x => x.TargetObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemPasswordSynchronisations_ConnectedSystems_Co~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemPasswordSynchronisations_ConnectedSystemId",
                table: "ConnectedSystemPasswordSynchronisations",
                column: "ConnectedSystemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemPasswordSynchronisations_Enabled",
                table: "ConnectedSystemPasswordSynchronisations",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemPasswordSynchronisations_TargetObjectTypeId",
                table: "ConnectedSystemPasswordSynchronisations",
                column: "TargetObjectTypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectedSystemPasswordSynchronisations");
        }
    }
}
