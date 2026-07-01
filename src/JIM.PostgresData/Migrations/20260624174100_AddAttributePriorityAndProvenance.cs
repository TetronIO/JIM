using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddAttributePriorityAndProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NullIsValue",
                table: "SyncRuleMappings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: false,
                defaultValue: 2147483647);

            migrationBuilder.AddColumn<int>(
                name: "ContributedBySyncRuleId",
                table: "MetaverseObjectAttributeValues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NullValue",
                table: "MetaverseObjectAttributeValues",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_ContributedBySyncRuleId",
                table: "MetaverseObjectAttributeValues",
                column: "ContributedBySyncRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectAttributeValues_SyncRules_ContributedBySyncR~",
                table: "MetaverseObjectAttributeValues",
                column: "ContributedBySyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectAttributeValues_SyncRules_ContributedBySyncR~",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectAttributeValues_ContributedBySyncRuleId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "NullIsValue",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "ContributedBySyncRuleId",
                table: "MetaverseObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "NullValue",
                table: "MetaverseObjectAttributeValues");
        }
    }
}
