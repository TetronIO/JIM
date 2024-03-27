using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncRuleScopingChange : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncRuleScopingCriteriaGroup",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ParentGroupId = table.Column<int>(type: "integer", nullable: true),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleScopingCriteriaGroup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteriaGroup_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteriaGroup_SyncRuleScopingCriteriaGroup_P~",
                        column: x => x.ParentGroupId,
                        principalTable: "SyncRuleScopingCriteriaGroup",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleScopingCriteria",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComparisonType = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    SyncRuleScopingCriteriaGroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleScopingCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteria_MetaverseAttributes_MetaverseAttrib~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroup_SyncRu~",
                        column: x => x.SyncRuleScopingCriteriaGroupId,
                        principalTable: "SyncRuleScopingCriteriaGroup",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteria_MetaverseAttributeId",
                table: "SyncRuleScopingCriteria",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroupId",
                table: "SyncRuleScopingCriteria",
                column: "SyncRuleScopingCriteriaGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteriaGroup_ParentGroupId",
                table: "SyncRuleScopingCriteriaGroup",
                column: "ParentGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteriaGroup_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroup",
                column: "SyncRuleId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncRuleScopingCriteria");

            migrationBuilder.DropTable(
                name: "SyncRuleScopingCriteriaGroup");
        }
    }
}
