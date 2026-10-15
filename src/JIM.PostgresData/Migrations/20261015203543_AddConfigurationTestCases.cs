using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationTestCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigurationTestCases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SyncRuleMappingId = table.Column<int>(type: "integer", nullable: true),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true),
                    ObjectMatchingRuleId = table.Column<int>(type: "integer", nullable: true),
                    RunAsOfUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InputsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ExpectationJson = table.Column<string>(type: "jsonb", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByType = table.Column<int>(type: "integer", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdatedByType = table.Column<int>(type: "integer", nullable: false),
                    LastUpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    LastUpdatedByName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationTestCases", x => x.Id);
                    table.CheckConstraint("CK_ConfigurationTestCases_CoversWhatItsKindTests", "(\"Kind\" IN (0, 1) AND \"SyncRuleMappingId\" IS NOT NULL AND \"SyncRuleId\" IS NULL AND \"ObjectMatchingRuleId\" IS NULL) OR (\"Kind\" = 2 AND \"SyncRuleMappingId\" IS NULL AND \"SyncRuleId\" IS NOT NULL AND \"ObjectMatchingRuleId\" IS NULL) OR (\"Kind\" = 3 AND \"SyncRuleMappingId\" IS NULL AND \"SyncRuleId\" IS NULL AND \"ObjectMatchingRuleId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ConfigurationTestCases_ObjectMatchingRules_ObjectMatchingRu~",
                        column: x => x.ObjectMatchingRuleId,
                        principalTable: "ObjectMatchingRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConfigurationTestCases_SyncRuleMappings_SyncRuleMappingId",
                        column: x => x.SyncRuleMappingId,
                        principalTable: "SyncRuleMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConfigurationTestCases_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationTestCases_ObjectMatchingRuleId",
                table: "ConfigurationTestCases",
                column: "ObjectMatchingRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationTestCases_SyncRuleId",
                table: "ConfigurationTestCases",
                column: "SyncRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationTestCases_SyncRuleMappingId",
                table: "ConfigurationTestCases",
                column: "SyncRuleMappingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigurationTestCases");
        }
    }
}
