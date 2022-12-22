using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectedSystemSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSetting_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemSetting");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSetting_ConnectedSystemSettingValue_ValueId",
                table: "ConnectedSystemSetting");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorDefinitionsFile_ConnectorDefinitions_ConnectorDefi~",
                table: "ConnectorDefinitionsFile");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectorDefinitionsFile",
                table: "ConnectorDefinitionsFile");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemSettingValue",
                table: "ConnectedSystemSettingValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemSetting",
                table: "ConnectedSystemSetting");

            migrationBuilder.RenameTable(
                name: "ConnectorDefinitionsFile",
                newName: "ConnectorDefinitionFiles");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemSettingValue",
                newName: "ConnectedSystemSettingValues");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemSetting",
                newName: "ConnectedSystemSettings");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectorDefinitionsFile_ConnectorDefinitionId",
                table: "ConnectorDefinitionFiles",
                newName: "IX_ConnectorDefinitionFiles_ConnectorDefinitionId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSetting_ValueId",
                table: "ConnectedSystemSettings",
                newName: "IX_ConnectedSystemSettings_ValueId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSetting_ConnectedSystemId",
                table: "ConnectedSystemSettings",
                newName: "IX_ConnectedSystemSettings_ConnectedSystemId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectorDefinitionFiles",
                table: "ConnectorDefinitionFiles",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemSettingValues",
                table: "ConnectedSystemSettingValues",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemSettings",
                table: "ConnectedSystemSettings",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ConnectedSystemPartition",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemPartition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemPartition_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemContainer",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartitionId = table.Column<int>(type: "integer", nullable: true),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectedSystemContainerId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemContainer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemContainer_ConnectedSystemContainer_Connected~",
                        column: x => x.ConnectedSystemContainerId,
                        principalTable: "ConnectedSystemContainer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemContainer_ConnectedSystemPartition_Partition~",
                        column: x => x.PartitionId,
                        principalTable: "ConnectedSystemPartition",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemContainer_ConnectedSystemContainerId",
                table: "ConnectedSystemContainer",
                column: "ConnectedSystemContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemContainer_PartitionId",
                table: "ConnectedSystemContainer",
                column: "PartitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemPartition_ConnectedSystemId",
                table: "ConnectedSystemPartition",
                column: "ConnectedSystemId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettings_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemSettings",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettings_ConnectedSystemSettingValues_ValueId",
                table: "ConnectedSystemSettings",
                column: "ValueId",
                principalTable: "ConnectedSystemSettingValues",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorDefinitionFiles_ConnectorDefinitions_ConnectorDefi~",
                table: "ConnectorDefinitionFiles",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettings_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettings_ConnectedSystemSettingValues_ValueId",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectorDefinitionFiles_ConnectorDefinitions_ConnectorDefi~",
                table: "ConnectorDefinitionFiles");

            migrationBuilder.DropTable(
                name: "ConnectedSystemContainer");

            migrationBuilder.DropTable(
                name: "ConnectedSystemPartition");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectorDefinitionFiles",
                table: "ConnectorDefinitionFiles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemSettingValues",
                table: "ConnectedSystemSettingValues");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemSettings",
                table: "ConnectedSystemSettings");

            migrationBuilder.RenameTable(
                name: "ConnectorDefinitionFiles",
                newName: "ConnectorDefinitionsFile");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemSettingValues",
                newName: "ConnectedSystemSettingValue");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemSettings",
                newName: "ConnectedSystemSetting");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectorDefinitionFiles_ConnectorDefinitionId",
                table: "ConnectorDefinitionsFile",
                newName: "IX_ConnectorDefinitionsFile_ConnectorDefinitionId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSettings_ValueId",
                table: "ConnectedSystemSetting",
                newName: "IX_ConnectedSystemSetting_ValueId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemSettings_ConnectedSystemId",
                table: "ConnectedSystemSetting",
                newName: "IX_ConnectedSystemSetting_ConnectedSystemId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectorDefinitionsFile",
                table: "ConnectorDefinitionsFile",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemSettingValue",
                table: "ConnectedSystemSettingValue",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemSetting",
                table: "ConnectedSystemSetting",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSetting_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemSetting",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSetting_ConnectedSystemSettingValue_ValueId",
                table: "ConnectedSystemSetting",
                column: "ValueId",
                principalTable: "ConnectedSystemSettingValue",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectorDefinitionsFile_ConnectorDefinitions_ConnectorDefi~",
                table: "ConnectorDefinitionsFile",
                column: "ConnectorDefinitionId",
                principalTable: "ConnectorDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
