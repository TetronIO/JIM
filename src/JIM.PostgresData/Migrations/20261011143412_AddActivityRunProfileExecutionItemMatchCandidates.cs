using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityRunProfileExecutionItemMatchCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityRunProfileExecutionItemMatchCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateKind = table.Column<int>(type: "integer", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayNameSnapshot = table.Column<string>(type: "text", nullable: true),
                    TypeNameSnapshot = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemNameSnapshot = table.Column<string>(type: "text", nullable: true),
                    MatchedValue = table.Column<string>(type: "text", nullable: true),
                    ObjectMatchingRuleId = table.Column<int>(type: "integer", nullable: true),
                    ObjectMatchingRuleDescriptionSnapshot = table.Column<string>(type: "text", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    CandidateTotal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityRunProfileExecutionItemMatchCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchCandidates_ActivityRunProfileExecutionItems",
                        column: x => x.ActivityRunProfileExecutionItemId,
                        principalTable: "ActivityRunProfileExecutionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItemMatchCandidates_RpeiId",
                table: "ActivityRunProfileExecutionItemMatchCandidates",
                column: "ActivityRunProfileExecutionItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityRunProfileExecutionItemMatchCandidates");
        }
    }
}
