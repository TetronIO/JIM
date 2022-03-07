using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class FixMetaverseAttributeMOTRel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseAttributes_MetaverseObjectTypes_MetaverseObjectTyp~",
                table: "MetaverseAttributes");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseAttributes_MetaverseObjectTypeId",
                table: "MetaverseAttributes");

            migrationBuilder.DropColumn(
                name: "MetaverseObjectTypeId",
                table: "MetaverseAttributes");

            migrationBuilder.CreateTable(
                name: "MetaverseAttributeMetaverseObjectType",
                columns: table => new
                {
                    AttributesId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectTypesId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseAttributeMetaverseObjectType", x => new { x.AttributesId, x.MetaverseObjectTypesId });
                    table.ForeignKey(
                        name: "FK_MetaverseAttributeMetaverseObjectType_MetaverseAttributes_A~",
                        column: x => x.AttributesId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseAttributeMetaverseObjectType_MetaverseObjectTypes_~",
                        column: x => x.MetaverseObjectTypesId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseAttributeMetaverseObjectType_MetaverseObjectTypesId",
                table: "MetaverseAttributeMetaverseObjectType",
                column: "MetaverseObjectTypesId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetaverseAttributeMetaverseObjectType");

            migrationBuilder.AddColumn<int>(
                name: "MetaverseObjectTypeId",
                table: "MetaverseAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseAttributes_MetaverseObjectTypeId",
                table: "MetaverseAttributes",
                column: "MetaverseObjectTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseAttributes_MetaverseObjectTypes_MetaverseObjectTyp~",
                table: "MetaverseAttributes",
                column: "MetaverseObjectTypeId",
                principalTable: "MetaverseObjectTypes",
                principalColumn: "Id");
        }
    }
}
