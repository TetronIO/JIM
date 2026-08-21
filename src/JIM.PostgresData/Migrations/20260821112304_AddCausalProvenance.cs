using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCausalProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "QueuedByRunProfileExecutionItemId",
                table: "PendingExports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CausalEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectSyncOutcomeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CauseRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CauseSyncOutcomeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CauseMetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CauseConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CausePendingExportId = table.Column<Guid>(type: "uuid", nullable: true),
                    CauseObjectTypeName = table.Column<string>(type: "text", nullable: true),
                    CauseObjectTypePluralName = table.Column<string>(type: "text", nullable: true),
                    EffectAttributeName = table.Column<string>(type: "text", nullable: true),
                    CauseDisplayName = table.Column<string>(type: "text", nullable: true),
                    EdgeType = table.Column<int>(type: "integer", nullable: false),
                    ReasonCode = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemName = table.Column<string>(type: "text", nullable: true),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true),
                    SyncRuleName = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CausalEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CausalEdges_ActivityRunProfileExecutionItems",
                        column: x => x.EffectRunProfileExecutionItemId,
                        principalTable: "ActivityRunProfileExecutionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CausalEdges_CauseMetaverseObjectId",
                table: "CausalEdges",
                column: "CauseMetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_CausalEdges_CauseRunProfileExecutionItemId",
                table: "CausalEdges",
                column: "CauseRunProfileExecutionItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CausalEdges_EffectRunProfileExecutionItemId",
                table: "CausalEdges",
                column: "EffectRunProfileExecutionItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CausalEdges");

            migrationBuilder.DropColumn(
                name: "QueuedByRunProfileExecutionItemId",
                table: "PendingExports");
        }
    }
}
