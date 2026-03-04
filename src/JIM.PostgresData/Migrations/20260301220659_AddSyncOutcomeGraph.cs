using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncOutcomeGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutcomeSummary",
                table: "ActivityRunProfileExecutionItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActivityRunProfileExecutionItemSyncOutcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentSyncOutcomeId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutcomeType = table.Column<int>(type: "integer", nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetEntityDescription = table.Column<string>(type: "text", nullable: true),
                    DetailCount = table.Column<int>(type: "integer", nullable: true),
                    DetailMessage = table.Column<string>(type: "text", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityRunProfileExecutionItemSyncOutcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncOutcomes_ActivityRunProfileExecutionItems",
                        column: x => x.ActivityRunProfileExecutionItemId,
                        principalTable: "ActivityRunProfileExecutionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncOutcomes_ParentSyncOutcome",
                        column: x => x.ParentSyncOutcomeId,
                        principalTable: "ActivityRunProfileExecutionItemSyncOutcomes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItemSyncOutcomes_ActivityRunProfileExecutionItemId",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                column: "ActivityRunProfileExecutionItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItemSyncOutcomes_ParentSyncOutco~",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                column: "ParentSyncOutcomeId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItemSyncOutcomes_RpeiId_OutcomeType",
                table: "ActivityRunProfileExecutionItemSyncOutcomes",
                columns: new[] { "ActivityRunProfileExecutionItemId", "OutcomeType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityRunProfileExecutionItemSyncOutcomes");

            migrationBuilder.DropColumn(
                name: "OutcomeSummary",
                table: "ActivityRunProfileExecutionItems");
        }
    }
}
