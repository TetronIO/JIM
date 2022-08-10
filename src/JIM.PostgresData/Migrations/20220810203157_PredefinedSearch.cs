using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class PredefinedSearch : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PredefinedSearches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearches_MetaverseObjectTypes_MetaverseObjectType~",
                        column: x => x.MetaverseObjectTypeId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearches_MetaverseObjectTypeId",
                table: "PredefinedSearches",
                column: "MetaverseObjectTypeId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetaverseAttributePredefinedSearch");

            migrationBuilder.DropTable(
                name: "PredefinedSearches");
        }
    }
}
