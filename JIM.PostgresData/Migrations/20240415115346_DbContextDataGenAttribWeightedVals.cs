using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextDataGenAttribWeightedVals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DataGenerationTemplateAttributeWeightedValue_DataGeneration~",
                table: "DataGenerationTemplateAttributeWeightedValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DataGenerationTemplateAttributeWeightedValue",
                table: "DataGenerationTemplateAttributeWeightedValue");

            migrationBuilder.RenameTable(
                name: "DataGenerationTemplateAttributeWeightedValue",
                newName: "DataGenerationTemplateAttributeWeightedValues");

            migrationBuilder.RenameIndex(
                name: "IX_DataGenerationTemplateAttributeWeightedValue_DataGeneration~",
                table: "DataGenerationTemplateAttributeWeightedValues",
                newName: "IX_DataGenerationTemplateAttributeWeightedValues_DataGeneratio~");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DataGenerationTemplateAttributeWeightedValues",
                table: "DataGenerationTemplateAttributeWeightedValues",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_DataGenerationTemplateAttributeWeightedValues_DataGeneratio~",
                table: "DataGenerationTemplateAttributeWeightedValues",
                column: "DataGenerationTemplateAttributeId",
                principalTable: "DataGenerationTemplateAttributes",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DataGenerationTemplateAttributeWeightedValues_DataGeneratio~",
                table: "DataGenerationTemplateAttributeWeightedValues");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DataGenerationTemplateAttributeWeightedValues",
                table: "DataGenerationTemplateAttributeWeightedValues");

            migrationBuilder.RenameTable(
                name: "DataGenerationTemplateAttributeWeightedValues",
                newName: "DataGenerationTemplateAttributeWeightedValue");

            migrationBuilder.RenameIndex(
                name: "IX_DataGenerationTemplateAttributeWeightedValues_DataGeneratio~",
                table: "DataGenerationTemplateAttributeWeightedValue",
                newName: "IX_DataGenerationTemplateAttributeWeightedValue_DataGeneration~");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DataGenerationTemplateAttributeWeightedValue",
                table: "DataGenerationTemplateAttributeWeightedValue",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_DataGenerationTemplateAttributeWeightedValue_DataGeneration~",
                table: "DataGenerationTemplateAttributeWeightedValue",
                column: "DataGenerationTemplateAttributeId",
                principalTable: "DataGenerationTemplateAttributes",
                principalColumn: "Id");
        }
    }
}
