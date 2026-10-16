using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExampleDataTemplateAttributeConnectedSystemAttribute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataTemplateAttributes_ConnectedSystemAttributes_Con~",
                table: "ExampleDataTemplateAttributes");

            migrationBuilder.DropIndex(
                name: "IX_ExampleDataTemplateAttributes_ConnectedSystemObjectTypeAttr~",
                table: "ExampleDataTemplateAttributes");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemObjectTypeAttributeId",
                table: "ExampleDataTemplateAttributes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemObjectTypeAttributeId",
                table: "ExampleDataTemplateAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExampleDataTemplateAttributes_ConnectedSystemObjectTypeAttr~",
                table: "ExampleDataTemplateAttributes",
                column: "ConnectedSystemObjectTypeAttributeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataTemplateAttributes_ConnectedSystemAttributes_Con~",
                table: "ExampleDataTemplateAttributes",
                column: "ConnectedSystemObjectTypeAttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id");
        }
    }
}
