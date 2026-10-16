using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddJoinObjectMatchingRuleProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JoinMatchValue",
                table: "ConnectedSystemObjects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JoinObjectMatchingRuleId",
                table: "ConnectedSystemObjects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_JoinObjectMatchingRuleId",
                table: "ConnectedSystemObjects",
                column: "JoinObjectMatchingRuleId",
                filter: "\"JoinObjectMatchingRuleId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjects_ObjectMatchingRules_JoinObjectMatchi~",
                table: "ConnectedSystemObjects",
                column: "JoinObjectMatchingRuleId",
                principalTable: "ObjectMatchingRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjects_ObjectMatchingRules_JoinObjectMatchi~",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjects_JoinObjectMatchingRuleId",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "JoinMatchValue",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "JoinObjectMatchingRuleId",
                table: "ConnectedSystemObjects");
        }
    }
}
