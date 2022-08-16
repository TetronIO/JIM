using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class PDSCriteria : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PredefinedSearchCriteriaGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    PredefinedSearchId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearchCriteriaGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearches_Predefine~",
                        column: x => x.PredefinedSearchId,
                        principalTable: "PredefinedSearches",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PredefinedSearchCriteria",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComparisonType = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    PredefinedSearchCriteriaGroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearchCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchCriteria_MetaverseAttributes_MetaverseAttri~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchCriteria_PredefinedSearchCriteriaGroups_Pre~",
                        column: x => x.PredefinedSearchCriteriaGroupId,
                        principalTable: "PredefinedSearchCriteriaGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteria_MetaverseAttributeId",
                table: "PredefinedSearchCriteria",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteria_PredefinedSearchCriteriaGroupId",
                table: "PredefinedSearchCriteria",
                column: "PredefinedSearchCriteriaGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteriaGroups_PredefinedSearchId",
                table: "PredefinedSearchCriteriaGroups",
                column: "PredefinedSearchId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PredefinedSearchCriteria");

            migrationBuilder.DropTable(
                name: "PredefinedSearchCriteriaGroups");
        }
    }
}
