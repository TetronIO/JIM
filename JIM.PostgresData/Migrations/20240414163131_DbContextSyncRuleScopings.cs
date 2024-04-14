using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DbContextSyncRuleScopings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroup_SyncRu~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroup_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroup");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroup_SyncRuleScopingCriteriaGroup_P~",
                table: "SyncRuleScopingCriteriaGroup");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleScopingCriteriaGroup",
                table: "SyncRuleScopingCriteriaGroup");

            migrationBuilder.RenameTable(
                name: "SyncRuleScopingCriteriaGroup",
                newName: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleScopingCriteriaGroup_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups",
                newName: "IX_SyncRuleScopingCriteriaGroups_SyncRuleId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleScopingCriteriaGroup_ParentGroupId",
                table: "SyncRuleScopingCriteriaGroups",
                newName: "IX_SyncRuleScopingCriteriaGroups_ParentGroupId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleScopingCriteriaGroups",
                table: "SyncRuleScopingCriteriaGroups",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroups_SyncR~",
                table: "SyncRuleScopingCriteria",
                column: "SyncRuleScopingCriteriaGroupId",
                principalTable: "SyncRuleScopingCriteriaGroups",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRuleScopingCriteriaGroups~",
                table: "SyncRuleScopingCriteriaGroups",
                column: "ParentGroupId",
                principalTable: "SyncRuleScopingCriteriaGroups",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroups_SyncR~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRuleScopingCriteriaGroups~",
                table: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SyncRuleScopingCriteriaGroups",
                table: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.RenameTable(
                name: "SyncRuleScopingCriteriaGroups",
                newName: "SyncRuleScopingCriteriaGroup");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleScopingCriteriaGroups_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroup",
                newName: "IX_SyncRuleScopingCriteriaGroup_SyncRuleId");

            migrationBuilder.RenameIndex(
                name: "IX_SyncRuleScopingCriteriaGroups_ParentGroupId",
                table: "SyncRuleScopingCriteriaGroup",
                newName: "IX_SyncRuleScopingCriteriaGroup_ParentGroupId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SyncRuleScopingCriteriaGroup",
                table: "SyncRuleScopingCriteriaGroup",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroup_SyncRu~",
                table: "SyncRuleScopingCriteria",
                column: "SyncRuleScopingCriteriaGroupId",
                principalTable: "SyncRuleScopingCriteriaGroup",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroup_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroup",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroup_SyncRuleScopingCriteriaGroup_P~",
                table: "SyncRuleScopingCriteriaGroup",
                column: "ParentGroupId",
                principalTable: "SyncRuleScopingCriteriaGroup",
                principalColumn: "Id");
        }
    }
}
