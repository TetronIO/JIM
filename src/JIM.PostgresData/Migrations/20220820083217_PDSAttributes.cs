using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class PDSAttributes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetaverseAttributePredefinedSearch");

            migrationBuilder.CreateTable(
                name: "PredefinedSearchAttribute",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PredefinedSearchId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearchAttribute", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchAttribute_MetaverseAttributes_MetaverseAttr~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchAttribute_PredefinedSearches_PredefinedSear~",
                        column: x => x.PredefinedSearchId,
                        principalTable: "PredefinedSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchAttribute_MetaverseAttributeId",
                table: "PredefinedSearchAttribute",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchAttribute_PredefinedSearchId",
                table: "PredefinedSearchAttribute",
                column: "PredefinedSearchId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PredefinedSearchAttribute");

            migrationBuilder.CreateTable(
                name: "MetaverseAttributePredefinedSearch",
                columns: table => new
                {
                    MetaverseAttributesId = table.Column<int>(type: "integer", nullable: false),
                    PredefinedSearchesId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseAttributePredefinedSearch", x => new { x.MetaverseAttributesId, x.PredefinedSearchesId });
                    table.ForeignKey(
                        name: "FK_MetaverseAttributePredefinedSearch_MetaverseAttributes_Meta~",
                        column: x => x.MetaverseAttributesId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseAttributePredefinedSearch_PredefinedSearches_Prede~",
                        column: x => x.PredefinedSearchesId,
                        principalTable: "PredefinedSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseAttributePredefinedSearch_PredefinedSearchesId",
                table: "MetaverseAttributePredefinedSearch",
                column: "PredefinedSearchesId");
        }
    }
}
