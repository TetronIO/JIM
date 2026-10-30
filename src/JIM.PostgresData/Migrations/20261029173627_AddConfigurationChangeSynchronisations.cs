using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationChangeSynchronisations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigurationChangeSynchronisations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfigurationChangeActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ClaimedByActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationChangeSynchronisations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangeSynchronisations_Activities_ClaimedByAct~",
                        column: x => x.ClaimedByActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangeSynchronisations_Activities_Configuratio~",
                        column: x => x.ConfigurationChangeActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangeSynchronisations_ConnectedSystemObjectTy~",
                        column: x => x.ConnectedSystemObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangeSynchronisations_ConnectedSystems_Connec~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConfigurationChangeSynchronisations_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangeSynchronisations_ClaimedByActivityId",
                table: "ConfigurationChangeSynchronisations",
                column: "ClaimedByActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangeSynchronisations_ConfigurationChangeActi~",
                table: "ConfigurationChangeSynchronisations",
                column: "ConfigurationChangeActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangeSynchronisations_ConnectedSystemId_Awaiting",
                table: "ConfigurationChangeSynchronisations",
                column: "ConnectedSystemId",
                filter: "\"AppliedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangeSynchronisations_ConnectedSystemId_ObjectTypeId",
                table: "ConfigurationChangeSynchronisations",
                columns: new[] { "ConnectedSystemId", "ConnectedSystemObjectTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangeSynchronisations_ConnectedSystemObjectTy~",
                table: "ConfigurationChangeSynchronisations",
                column: "ConnectedSystemObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationChangeSynchronisations_SyncRuleId",
                table: "ConfigurationChangeSynchronisations",
                column: "SyncRuleId");

            migrationBuilder.AddColumn<bool>(
                name: "ConfigurationChangeNeedsFullSynchronisation",
                table: "Activities",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigurationChangeSynchronisations");

            migrationBuilder.DropColumn(
                name: "ConfigurationChangeNeedsFullSynchronisation",
                table: "Activities");
        }
    }
}
