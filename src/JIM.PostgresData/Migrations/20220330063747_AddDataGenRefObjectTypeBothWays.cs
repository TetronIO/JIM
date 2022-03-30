using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class AddDataGenRefObjectTypeBothWays : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectTypes_DataGenerationTemplateAttributes_DataG~",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectTypes_DataGenerationTemplateAttributeId",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "DataGenerationTemplateAttributeId",
                table: "MetaverseObjectTypes");

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributeMetaverseObjectType",
                columns: table => new
                {
                    DataGenerationTemplateAttributesId = table.Column<int>(type: "integer", nullable: false),
                    ReferenceMetaverseObjectTypesId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeMetaverseObjectType", x => new { x.DataGenerationTemplateAttributesId, x.ReferenceMetaverseObjectTypesId });
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeMetaverseObjectType_DataGene~",
                        column: x => x.DataGenerationTemplateAttributesId,
                        principalTable: "DataGenerationTemplateAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeMetaverseObjectType_Metavers~",
                        column: x => x.ReferenceMetaverseObjectTypesId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributeMetaverseObjectType_Referenc~",
                table: "DataGenerationTemplateAttributeMetaverseObjectType",
                column: "ReferenceMetaverseObjectTypesId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeMetaverseObjectType");

            migrationBuilder.AddColumn<int>(
                name: "DataGenerationTemplateAttributeId",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectTypes_DataGenerationTemplateAttributeId",
                table: "MetaverseObjectTypes",
                column: "DataGenerationTemplateAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectTypes_DataGenerationTemplateAttributes_DataG~",
                table: "MetaverseObjectTypes",
                column: "DataGenerationTemplateAttributeId",
                principalTable: "DataGenerationTemplateAttributes",
                principalColumn: "Id");
        }
    }
}
