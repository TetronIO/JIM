using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CascadeOwnedConfigurationDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers");

            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_SyncRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMappings_SyncRuleMappingId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroups_SyncR~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRuleScopingCriteriaGroups~",
                table: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers",
                column: "ParentContainerId",
                principalTable: "ConnectedSystemContainers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules",
                column: "ConnectedSystemObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_SyncRuleId",
                table: "SyncRuleMappings",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMappings_SyncRuleMappingId",
                table: "SyncRuleMappingSources",
                column: "SyncRuleMappingId",
                principalTable: "SyncRuleMappings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroups_SyncR~",
                table: "SyncRuleScopingCriteria",
                column: "SyncRuleScopingCriteriaGroupId",
                principalTable: "SyncRuleScopingCriteriaGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRuleScopingCriteriaGroups~",
                table: "SyncRuleScopingCriteriaGroups",
                column: "ParentGroupId",
                principalTable: "SyncRuleScopingCriteriaGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers");

            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_SyncRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMappings_SyncRuleMappingId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroups_SyncR~",
                table: "SyncRuleScopingCriteria");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRuleScopingCriteriaGroups~",
                table: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                table: "ConnectedSystemContainers",
                column: "ParentContainerId",
                principalTable: "ConnectedSystemContainers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                table: "ObjectMatchingRules",
                column: "ConnectedSystemObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                table: "ObjectMatchingRules",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_SyncRuleId",
                table: "SyncRuleMappings",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_SyncRuleMappings_SyncRuleMappingId",
                table: "SyncRuleMappingSources",
                column: "SyncRuleMappingId",
                principalTable: "SyncRuleMappings",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroups_SyncR~",
                table: "SyncRuleScopingCriteria",
                column: "SyncRuleScopingCriteriaGroupId",
                principalTable: "SyncRuleScopingCriteriaGroups",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRuleScopingCriteriaGroups~",
                table: "SyncRuleScopingCriteriaGroups",
                column: "ParentGroupId",
                principalTable: "SyncRuleScopingCriteriaGroups",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleScopingCriteriaGroups_SyncRules_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");
        }
    }
}
