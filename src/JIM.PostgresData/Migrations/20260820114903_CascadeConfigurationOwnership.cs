using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CascadeConfigurationOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorDefinitionSettings_ConnectorDefinitions_ConnectorD~",
                table: "ConnectorDefinitionSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataObjectTypes_ExampleDataTemplates_ExampleDataTemp~",
                table: "ExampleDataObjectTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataTemplateAttributes_ExampleDataObjectTypes_Exampl~",
                table: "ExampleDataTemplateAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataTemplateAttributeWeightedValues_ExampleDataTempl~",
                table: "ExampleDataTemplateAttributeWeightedValues");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchCriteria_PredefinedSearchCriteriaGroups_Pre~",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearchCriteriaGrou~",
                table: "PredefinedSearchCriteriaGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearches_Predefine~",
                table: "PredefinedSearchCriteriaGroups");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorDefinitionSettings_ConnectorDefinitions_ConnectorD~",
                table: "ConnectorDefinitionSettings",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataObjectTypes_ExampleDataTemplates_ExampleDataTemp~",
                table: "ExampleDataObjectTypes",
                column: "ExampleDataTemplateId",
                principalTable: "ExampleDataTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues",
                column: "ExampleDataSetId",
                principalTable: "ExampleDataSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataTemplateAttributes_ExampleDataObjectTypes_Exampl~",
                table: "ExampleDataTemplateAttributes",
                column: "ExampleDataObjectTypeId",
                principalTable: "ExampleDataObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataTemplateAttributeWeightedValues_ExampleDataTempl~",
                table: "ExampleDataTemplateAttributeWeightedValues",
                column: "ExampleDataTemplateAttributeId",
                principalTable: "ExampleDataTemplateAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchCriteria_PredefinedSearchCriteriaGroups_Pre~",
                table: "PredefinedSearchCriteria",
                column: "PredefinedSearchCriteriaGroupId",
                principalTable: "PredefinedSearchCriteriaGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearchCriteriaGrou~",
                table: "PredefinedSearchCriteriaGroups",
                column: "ParentGroupId",
                principalTable: "PredefinedSearchCriteriaGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearches_Predefine~",
                table: "PredefinedSearchCriteriaGroups",
                column: "PredefinedSearchId",
                principalTable: "PredefinedSearches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorDefinitionSettings_ConnectorDefinitions_ConnectorD~",
                table: "ConnectorDefinitionSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataObjectTypes_ExampleDataTemplates_ExampleDataTemp~",
                table: "ExampleDataObjectTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataTemplateAttributes_ExampleDataObjectTypes_Exampl~",
                table: "ExampleDataTemplateAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataTemplateAttributeWeightedValues_ExampleDataTempl~",
                table: "ExampleDataTemplateAttributeWeightedValues");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchCriteria_PredefinedSearchCriteriaGroups_Pre~",
                table: "PredefinedSearchCriteria");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearchCriteriaGrou~",
                table: "PredefinedSearchCriteriaGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearches_Predefine~",
                table: "PredefinedSearchCriteriaGroups");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorDefinitionSettings_ConnectorDefinitions_ConnectorD~",
                table: "ConnectorDefinitionSettings",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataObjectTypes_ExampleDataTemplates_ExampleDataTemp~",
                table: "ExampleDataObjectTypes",
                column: "ExampleDataTemplateId",
                principalTable: "ExampleDataTemplates",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues",
                column: "ExampleDataSetId",
                principalTable: "ExampleDataSets",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataTemplateAttributes_ExampleDataObjectTypes_Exampl~",
                table: "ExampleDataTemplateAttributes",
                column: "ExampleDataObjectTypeId",
                principalTable: "ExampleDataObjectTypes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataTemplateAttributeWeightedValues_ExampleDataTempl~",
                table: "ExampleDataTemplateAttributeWeightedValues",
                column: "ExampleDataTemplateAttributeId",
                principalTable: "ExampleDataTemplateAttributes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchCriteria_PredefinedSearchCriteriaGroups_Pre~",
                table: "PredefinedSearchCriteria",
                column: "PredefinedSearchCriteriaGroupId",
                principalTable: "PredefinedSearchCriteriaGroups",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearchCriteriaGrou~",
                table: "PredefinedSearchCriteriaGroups",
                column: "ParentGroupId",
                principalTable: "PredefinedSearchCriteriaGroups",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearches_Predefine~",
                table: "PredefinedSearchCriteriaGroups",
                column: "PredefinedSearchId",
                principalTable: "PredefinedSearches",
                principalColumn: "Id");
        }
    }
}
