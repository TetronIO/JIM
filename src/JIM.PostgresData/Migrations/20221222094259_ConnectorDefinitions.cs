using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectorDefinitions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectorDefinitionId",
                table: "ConnectedSystems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConnectedSystemSettingValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    StringEncryptedValue = table.Column<string>(type: "text", nullable: true),
                    CheckboxValue = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemSettingValue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsFullImport = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsDeltaImport = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsExport = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemSetting",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ValueId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    DefaultCheckboxValue = table.Column<bool>(type: "boolean", nullable: true),
                    DefaultStringValue = table.Column<string>(type: "text", nullable: true),
                    DropDownValues = table.Column<List<string>>(type: "text[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemSetting", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemSetting_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemSetting_ConnectedSystemSettingValue_ValueId",
                        column: x => x.ValueId,
                        principalTable: "ConnectedSystemSettingValue",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectorDefinitionsFile",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectorDefinitionId = table.Column<int>(type: "integer", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    ImplementsIConnector = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsICapabilities = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsISchema = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsISettings = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIContainers = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIExportUsingCalls = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIExportUsingFiles = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIImportUsingCalls = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIImportUsingFiles = table.Column<bool>(type: "boolean", nullable: false),
                    FileSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    File = table.Column<byte[]>(type: "bytea", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDefinitionsFile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectorDefinitionsFile_ConnectorDefinitions_ConnectorDefi~",
                        column: x => x.ConnectorDefinitionId,
                        principalTable: "ConnectorDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystems_ConnectorDefinitionId",
                table: "ConnectedSystems",
                column: "ConnectorDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributes_ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSetting_ConnectedSystemId",
                table: "ConnectedSystemSetting",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSetting_ValueId",
                table: "ConnectedSystemSetting",
                column: "ValueId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorDefinitionsFile_ConnectorDefinitionId",
                table: "ConnectorDefinitionsFile",
                column: "ConnectorDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Connec~",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystems_ConnectorDefinitions_ConnectorDefinitionId",
                table: "ConnectedSystems",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Connec~",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystems_ConnectorDefinitions_ConnectorDefinitionId",
                table: "ConnectedSystems");

            migrationBuilder.DropTable(
                name: "ConnectedSystemSetting");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitionsFile");

            migrationBuilder.DropTable(
                name: "ConnectedSystemSettingValue");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystems_ConnectorDefinitionId",
                table: "ConnectedSystems");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemAttributes_ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes");

            migrationBuilder.DropColumn(
                name: "ConnectorDefinitionId",
                table: "ConnectedSystems");

            migrationBuilder.DropColumn(
                name: "ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes");
        }
    }
}
