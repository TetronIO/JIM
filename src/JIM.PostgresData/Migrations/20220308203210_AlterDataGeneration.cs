using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class AlterDataGeneration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExampleDataSetId",
                table: "DataGenerationTemplateAttributes");

            migrationBuilder.DropColumn(
                name: "ExampleDataValueId",
                table: "DataGenerationTemplateAttributes");

            migrationBuilder.AddColumn<List<int>>(
                name: "ExampleDataSetIds",
                table: "DataGenerationTemplateAttributes",
                type: "integer[]",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectTypes_Name",
                table: "MetaverseObjectTypes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplates_Name",
                table: "DataGenerationTemplates",
                column: "Name");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectTypes_Name",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropIndex(
                name: "IX_DataGenerationTemplates_Name",
                table: "DataGenerationTemplates");

            migrationBuilder.DropColumn(
                name: "ExampleDataSetIds",
                table: "DataGenerationTemplateAttributes");

            migrationBuilder.AddColumn<int>(
                name: "ExampleDataSetId",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExampleDataValueId",
                table: "DataGenerationTemplateAttributes",
                type: "integer",
                nullable: true);
        }
    }
}
