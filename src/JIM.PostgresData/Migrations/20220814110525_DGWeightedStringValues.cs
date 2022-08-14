using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DGWeightedStringValues : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributeWeightedValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Weight = table.Column<float>(type: "real", nullable: false),
                    DataGenerationTemplateAttributeId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeWeightedValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeWeightedValue_DataGeneration~",
                        column: x => x.DataGenerationTemplateAttributeId,
                        principalTable: "DataGenerationTemplateAttributes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributeWeightedValue_DataGeneration~",
                table: "DataGenerationTemplateAttributeWeightedValue",
                column: "DataGenerationTemplateAttributeId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeWeightedValue");
        }
    }
}
