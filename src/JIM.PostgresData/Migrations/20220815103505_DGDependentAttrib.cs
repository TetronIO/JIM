using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DGDependentAttrib : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttributeDependencyId",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributeDependency",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: false),
                    ComparisonType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeDependency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeDependency_MetaverseAttribut~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_AttributeDependencyId",
                table: "DataGenerationTemplateAttributes",
                column: "AttributeDependencyId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributeDependency_MetaverseAttribut~",
                table: "DataGenerationTemplateAttributeDependency",
                column: "MetaverseAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_DataGenerationTemplateAttributes_DataGenerationTemplateAttr~",
                table: "DataGenerationTemplateAttributes",
                column: "AttributeDependencyId",
                principalTable: "DataGenerationTemplateAttributeDependency",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DataGenerationTemplateAttributes_DataGenerationTemplateAttr~",
                table: "DataGenerationTemplateAttributes");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeDependency");

            migrationBuilder.DropIndex(
                name: "IX_DataGenerationTemplateAttributes_AttributeDependencyId",
                table: "DataGenerationTemplateAttributes");

            migrationBuilder.DropColumn(
                name: "AttributeDependencyId",
                table: "DataGenerationTemplateAttributes");
        }
    }
}
