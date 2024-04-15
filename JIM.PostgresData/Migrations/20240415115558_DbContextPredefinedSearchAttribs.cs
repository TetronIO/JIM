using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextPredefinedSearchAttribs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchAttribute_MetaverseAttributes_MetaverseAttr~",
                table: "PredefinedSearchAttribute");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchAttribute_PredefinedSearches_PredefinedSear~",
                table: "PredefinedSearchAttribute");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PredefinedSearchAttribute",
                table: "PredefinedSearchAttribute");

            migrationBuilder.RenameTable(
                name: "PredefinedSearchAttribute",
                newName: "PredefinedSearchAttributes");

            migrationBuilder.RenameIndex(
                name: "IX_PredefinedSearchAttribute_PredefinedSearchId",
                table: "PredefinedSearchAttributes",
                newName: "IX_PredefinedSearchAttributes_PredefinedSearchId");

            migrationBuilder.RenameIndex(
                name: "IX_PredefinedSearchAttribute_MetaverseAttributeId",
                table: "PredefinedSearchAttributes",
                newName: "IX_PredefinedSearchAttributes_MetaverseAttributeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PredefinedSearchAttributes",
                table: "PredefinedSearchAttributes",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchAttributes_MetaverseAttributes_MetaverseAtt~",
                table: "PredefinedSearchAttributes",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchAttributes_PredefinedSearches_PredefinedSea~",
                table: "PredefinedSearchAttributes",
                column: "PredefinedSearchId",
                principalTable: "PredefinedSearches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchAttributes_MetaverseAttributes_MetaverseAtt~",
                table: "PredefinedSearchAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchAttributes_PredefinedSearches_PredefinedSea~",
                table: "PredefinedSearchAttributes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PredefinedSearchAttributes",
                table: "PredefinedSearchAttributes");

            migrationBuilder.RenameTable(
                name: "PredefinedSearchAttributes",
                newName: "PredefinedSearchAttribute");

            migrationBuilder.RenameIndex(
                name: "IX_PredefinedSearchAttributes_PredefinedSearchId",
                table: "PredefinedSearchAttribute",
                newName: "IX_PredefinedSearchAttribute_PredefinedSearchId");

            migrationBuilder.RenameIndex(
                name: "IX_PredefinedSearchAttributes_MetaverseAttributeId",
                table: "PredefinedSearchAttribute",
                newName: "IX_PredefinedSearchAttribute_MetaverseAttributeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PredefinedSearchAttribute",
                table: "PredefinedSearchAttribute",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchAttribute_MetaverseAttributes_MetaverseAttr~",
                table: "PredefinedSearchAttribute",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchAttribute_PredefinedSearches_PredefinedSear~",
                table: "PredefinedSearchAttribute",
                column: "PredefinedSearchId",
                principalTable: "PredefinedSearches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
