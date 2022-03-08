using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class FixExampleDataSetAttribRefs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataSets_DataGenerationTemplateAttributes_DataGenera~",
                table: "ExampleDataSets");

            migrationBuilder.DropIndex(
                name: "IX_ExampleDataSets_DataGenerationTemplateAttributeId",
                table: "ExampleDataSets");

            migrationBuilder.DropColumn(
                name: "DataGenerationTemplateAttributeId",
                table: "ExampleDataSets");

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributeExampleDataSet",
                columns: table => new
                {
                    DataGenerationTemplateAttributesId = table.Column<int>(type: "integer", nullable: false),
                    ExampleDataSetsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeExampleDataSet", x => new { x.DataGenerationTemplateAttributesId, x.ExampleDataSetsId });
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeExampleDataSet_DataGeneratio~",
                        column: x => x.DataGenerationTemplateAttributesId,
                        principalTable: "DataGenerationTemplateAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeExampleDataSet_ExampleDataSe~",
                        column: x => x.ExampleDataSetsId,
                        principalTable: "ExampleDataSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributeExampleDataSet_ExampleDataSe~",
                table: "DataGenerationTemplateAttributeExampleDataSet",
                column: "ExampleDataSetsId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeExampleDataSet");

            migrationBuilder.AddColumn<int>(
                name: "DataGenerationTemplateAttributeId",
                table: "ExampleDataSets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExampleDataSets_DataGenerationTemplateAttributeId",
                table: "ExampleDataSets",
                column: "DataGenerationTemplateAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataSets_DataGenerationTemplateAttributes_DataGenera~",
                table: "ExampleDataSets",
                column: "DataGenerationTemplateAttributeId",
                principalTable: "DataGenerationTemplateAttributes",
                principalColumn: "Id");
        }
    }
}
