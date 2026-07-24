using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveObjectMatchingRuleSourceMetaverseAttribute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRuleSources_MetaverseAttributes_MetaverseAttr~",
                table: "ObjectMatchingRuleSources");

            migrationBuilder.DropIndex(
                name: "IX_ObjectMatchingRuleSources_MetaverseAttributeId",
                table: "ObjectMatchingRuleSources");

            migrationBuilder.DropColumn(
                name: "MetaverseAttributeId",
                table: "ObjectMatchingRuleSources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MetaverseAttributeId",
                table: "ObjectMatchingRuleSources",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSources_MetaverseAttributeId",
                table: "ObjectMatchingRuleSources",
                column: "MetaverseAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRuleSources_MetaverseAttributes_MetaverseAttr~",
                table: "ObjectMatchingRuleSources",
                column: "MetaverseAttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id");
        }
    }
}
