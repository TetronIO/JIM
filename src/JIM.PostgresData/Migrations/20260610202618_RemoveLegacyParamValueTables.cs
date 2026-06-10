using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyParamValueTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ObjectMatchingRuleSourceParamValues");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingSourceParamValues");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObjectMatchingRuleSourceParamValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ObjectMatchingRuleSourceId = table.Column<int>(type: "integer", nullable: false),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: false),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoubleValue = table.Column<double>(type: "double precision", nullable: false),
                    IntValue = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "SyncRuleMappingSourceParamValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: false),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoubleValue = table.Column<double>(type: "double precision", nullable: false),
                    IntValue = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappingSourceParamValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValues_ConnectedSystemAttributes_~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValues_MetaverseAttributes_Metave~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_ConnectedSystemAttribut~",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_MetaverseAttributeId",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_ObjectMatchingRuleSourc~",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "ObjectMatchingRuleSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSourceParamValues",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_MetaverseAttributeId",
                table: "SyncRuleMappingSourceParamValues",
                column: "MetaverseAttributeId");
        }
    }
}
