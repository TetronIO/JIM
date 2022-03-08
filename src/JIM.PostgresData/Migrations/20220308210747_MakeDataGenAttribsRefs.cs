using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class MakeDataGenAttribsRefs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExampleDataSetIds",
                table: "DataGenerationTemplateAttributes");

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

        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddColumn<List<int>>(
                name: "ExampleDataSetIds",
                table: "DataGenerationTemplateAttributes",
                type: "integer[]",
                nullable: false);
        }
    }
}
