using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DataGenTempAttribCSOTAttribFix : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ConnectedSystemAttributeId",
                table: "DataGenerationTemplateAttributes",
                newName: "ConnectedSystemObjectTypeAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_DataGenerationTemplateAttributes_ConnectedSystemAttributeId",
                table: "DataGenerationTemplateAttributes",
                newName: "IX_DataGenerationTemplateAttributes_ConnectedSystemObjectTypeA~");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ConnectedSystemObjectTypeAttributeId",
                table: "DataGenerationTemplateAttributes",
                newName: "ConnectedSystemAttributeId");

            migrationBuilder.RenameIndex(
                name: "IX_DataGenerationTemplateAttributes_ConnectedSystemObjectTypeA~",
                table: "DataGenerationTemplateAttributes",
                newName: "IX_DataGenerationTemplateAttributes_ConnectedSystemAttributeId");
        }
    }
}
