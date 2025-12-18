using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectMatchingRuleEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_AttributeFlowSynchronisationRule~",
                table: "SyncRuleMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_ObjectMatchingSynchronisationRul~",
                table: "SyncRuleMappings");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMappings_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMappings_ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMappings");

            // First, add the new SyncRuleId column (nullable initially)
            migrationBuilder.AddColumn<int>(
                name: "SyncRuleId",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: true);

            // Migrate data: copy AttributeFlowSynchronisationRuleId to SyncRuleId
            // (AttributeFlow mappings are the ones we want to keep - ObjectMatching mappings move to new table)
            migrationBuilder.Sql(
                @"UPDATE ""SyncRuleMappings""
                  SET ""SyncRuleId"" = COALESCE(""AttributeFlowSynchronisationRuleId"", ""ObjectMatchingSynchronisationRuleId"")");

            // Delete sources for any orphaned mappings first (to handle FK constraints)
            migrationBuilder.Sql(
                @"DELETE FROM ""SyncRuleMappingSources""
                  WHERE ""SyncRuleMappingId"" IN (
                      SELECT ""Id"" FROM ""SyncRuleMappings"" WHERE ""SyncRuleId"" IS NULL
                  )");

            // Now delete any orphaned mappings that don't have a valid SyncRuleId
            migrationBuilder.Sql(
                @"DELETE FROM ""SyncRuleMappings"" WHERE ""SyncRuleId"" IS NULL");

            // Now drop the old columns
            migrationBuilder.DropColumn(
                name: "AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "Order",
                table: "SyncRuleMappings");

            migrationBuilder.AddColumn<int>(
                name: "ObjectMatchingRuleMode",
                table: "ConnectedSystems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ObjectMatchingRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemObjectTypeId = table.Column<int>(type: "integer", nullable: true),
                    TargetMetaverseAttributeId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectMatchingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRules_ConnectedSystemObjectTypes_ConnectedSys~",
                        column: x => x.ConnectedSystemObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRules_MetaverseAttributes_TargetMetaverseAttr~",
                        column: x => x.TargetMetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRules_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ObjectMatchingRuleSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    ObjectMatchingRuleId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    FunctionId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectMatchingRuleSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSources_ConnectedSystemAttributes_Connect~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSources_Function_FunctionId",
                        column: x => x.FunctionId,
                        principalTable: "Function",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSources_MetaverseAttributes_MetaverseAttr~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSources_ObjectMatchingRules_ObjectMatchin~",
                        column: x => x.ObjectMatchingRuleId,
                        principalTable: "ObjectMatchingRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ObjectMatchingRuleSourceParamValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ObjectMatchingRuleSourceId = table.Column<int>(type: "integer", nullable: false),
                    FunctionParameterId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoubleValue = table.Column<double>(type: "double precision", nullable: false),
                    IntValue = table.Column<int>(type: "integer", nullable: false),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectMatchingRuleSourceParamValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSourceParamValues_ConnectedSystemAttribut~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSourceParamValues_FunctionParameter_Funct~",
                        column: x => x.FunctionParameterId,
                        principalTable: "FunctionParameter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSourceParamValues_MetaverseAttributes_Met~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ObjectMatchingRuleSourceParamValues_ObjectMatchingRuleSourc~",
                        column: x => x.ObjectMatchingRuleSourceId,
                        principalTable: "ObjectMatchingRuleSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_SyncRuleId",
                table: "SyncRuleMappings",
                column: "SyncRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRules_ConnectedSystemObjectTypeId",
                table: "ObjectMatchingRules",
                column: "ConnectedSystemObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRules_SyncRuleId",
                table: "ObjectMatchingRules",
                column: "SyncRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRules_TargetMetaverseAttributeId",
                table: "ObjectMatchingRules",
                column: "TargetMetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_ConnectedSystemAttribut~",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_FunctionParameterId",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "FunctionParameterId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_MetaverseAttributeId",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_ObjectMatchingRuleSourc~",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "ObjectMatchingRuleSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSources_ConnectedSystemAttributeId",
                table: "ObjectMatchingRuleSources",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSources_FunctionId",
                table: "ObjectMatchingRuleSources",
                column: "FunctionId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSources_MetaverseAttributeId",
                table: "ObjectMatchingRuleSources",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSources_ObjectMatchingRuleId",
                table: "ObjectMatchingRuleSources",
                column: "ObjectMatchingRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_SyncRuleId",
                table: "SyncRuleMappings",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_SyncRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.DropTable(
                name: "ObjectMatchingRuleSourceParamValues");

            migrationBuilder.DropTable(
                name: "ObjectMatchingRuleSources");

            migrationBuilder.DropTable(
                name: "ObjectMatchingRules");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMappings_SyncRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.DropColumn(
                name: "ObjectMatchingRuleMode",
                table: "ConnectedSystems");

            // Restore old columns
            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Migrate data back: copy SyncRuleId to AttributeFlowSynchronisationRuleId
            migrationBuilder.Sql(
                @"UPDATE ""SyncRuleMappings""
                  SET ""AttributeFlowSynchronisationRuleId"" = ""SyncRuleId"",
                      ""Type"" = 1");

            // Drop SyncRuleId column
            migrationBuilder.DropColumn(
                name: "SyncRuleId",
                table: "SyncRuleMappings");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMappings",
                column: "AttributeFlowSynchronisationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMappings",
                column: "ObjectMatchingSynchronisationRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_AttributeFlowSynchronisationRule~",
                table: "SyncRuleMappings",
                column: "AttributeFlowSynchronisationRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappings_SyncRules_ObjectMatchingSynchronisationRul~",
                table: "SyncRuleMappings",
                column: "ObjectMatchingSynchronisationRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");
        }
    }
}
