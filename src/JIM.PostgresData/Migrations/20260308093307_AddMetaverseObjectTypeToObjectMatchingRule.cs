using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaverseObjectTypeToObjectMatchingRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MetaverseObjectTypeId",
                table: "ObjectMatchingRules",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRules_MetaverseObjectTypeId",
                table: "ObjectMatchingRules",
                column: "MetaverseObjectTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_MetaverseObjectTypes_MetaverseObjectTyp~",
                table: "ObjectMatchingRules",
                column: "MetaverseObjectTypeId",
                principalTable: "MetaverseObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_MetaverseObjectTypes_MetaverseObjectTyp~",
                table: "ObjectMatchingRules");

            migrationBuilder.DropIndex(
                name: "IX_ObjectMatchingRules_MetaverseObjectTypeId",
                table: "ObjectMatchingRules");

            migrationBuilder.DropColumn(
                name: "MetaverseObjectTypeId",
                table: "ObjectMatchingRules");
        }
    }
}
