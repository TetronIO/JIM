using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class DataGeneration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataGenerationTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExampleDataSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Culture = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleDataSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationObjectTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    ObjectsToCreate = table.Column<int>(type: "integer", nullable: false),
                    DataGenerationTemplateId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationObjectTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationObjectTypes_DataGenerationTemplates_DataGener~",
                        column: x => x.DataGenerationTemplateId,
                        principalTable: "DataGenerationTemplates",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationObjectTypes_MetaverseObjectTypes_MetaverseObj~",
                        column: x => x.MetaverseObjectTypeId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExampleDataSetValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExampleDataSetId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleDataSetValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                        column: x => x.ExampleDataSetId,
                        principalTable: "ExampleDataSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    PopulatedValuesPercentage = table.Column<int>(type: "integer", nullable: false),
                    BoolTrueDistribution = table.Column<int>(type: "integer", nullable: true),
                    BoolShouldBeRandom = table.Column<bool>(type: "boolean", nullable: true),
                    MinDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MinNumber = table.Column<int>(type: "integer", nullable: true),
                    MaxNumber = table.Column<int>(type: "integer", nullable: true),
                    SequentialNumbers = table.Column<bool>(type: "boolean", nullable: true),
                    RandomNumbers = table.Column<bool>(type: "boolean", nullable: true),
                    Pattern = table.Column<string>(type: "text", nullable: true),
                    ExampleDataSetId = table.Column<int>(type: "integer", nullable: true),
                    ExampleDataValueId = table.Column<int>(type: "integer", nullable: true),
                    DataGenerationObjectTypeId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_ConnectedSystemAttributes_~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_DataGenerationObjectTypes_~",
                        column: x => x.DataGenerationObjectTypeId,
                        principalTable: "DataGenerationObjectTypes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_MetaverseAttributes_Metave~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationObjectTypes_DataGenerationTemplateId",
                table: "DataGenerationObjectTypes",
                column: "DataGenerationTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationObjectTypes_MetaverseObjectTypeId",
                table: "DataGenerationObjectTypes",
                column: "MetaverseObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_ConnectedSystemAttributeId",
                table: "DataGenerationTemplateAttributes",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_DataGenerationObjectTypeId",
                table: "DataGenerationTemplateAttributes",
                column: "DataGenerationObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_MetaverseAttributeId",
                table: "DataGenerationTemplateAttributes",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleDataSetValues_ExampleDataSetId",
                table: "ExampleDataSetValues",
                column: "ExampleDataSetId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributes");

            migrationBuilder.DropTable(
                name: "ExampleDataSetValues");

            migrationBuilder.DropTable(
                name: "DataGenerationObjectTypes");

            migrationBuilder.DropTable(
                name: "ExampleDataSets");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplates");
        }
    }
}
