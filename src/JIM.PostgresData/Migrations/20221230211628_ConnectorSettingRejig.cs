using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectorSettingRejig : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemRunProfile_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemRunProfile");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemRunProfile_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemRunProfile");

            migrationBuilder.DropTable(
                name: "ConnectedSystemSettings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemRunProfile",
                table: "ConnectedSystemRunProfile");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemRunProfile",
                newName: "ConnectedSystemRunProfiles");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemRunProfile_PartitionId",
                table: "ConnectedSystemRunProfiles",
                newName: "IX_ConnectedSystemRunProfiles_PartitionId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemRunProfile_ConnectedSystemId",
                table: "ConnectedSystemRunProfiles",
                newName: "IX_ConnectedSystemRunProfiles_ConnectedSystemId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemRunProfiles",
                table: "ConnectedSystemRunProfiles",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ConnectorDefinitionSetting",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    DefaultCheckboxValue = table.Column<bool>(type: "boolean", nullable: true),
                    DefaultStringValue = table.Column<string>(type: "text", nullable: true),
                    DropDownValues = table.Column<List<string>>(type: "text[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDefinitionSetting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemSettingValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    SettingId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    StringEncryptedValue = table.Column<string>(type: "text", nullable: true),
                    CheckboxValue = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemSettingValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemSettingValue_ConnectedSystems_ConnectedSyste~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSetting_Sett~",
                        column: x => x.SettingId,
                        principalTable: "ConnectorDefinitionSetting",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSettingValue_ConnectedSystemId",
                table: "ConnectedSystemSettingValue",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSettingValue_SettingId",
                table: "ConnectedSystemSettingValue",
                column: "SettingId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemRunProfiles_ConnectedSystemPartitions_Partit~",
                table: "ConnectedSystemRunProfiles",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemRunProfiles_ConnectedSystems_ConnectedSystem~",
                table: "ConnectedSystemRunProfiles",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemRunProfiles_ConnectedSystemPartitions_Partit~",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemRunProfiles_ConnectedSystems_ConnectedSystem~",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropTable(
                name: "ConnectedSystemSettingValue");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitionSetting");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ConnectedSystemRunProfiles",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.RenameTable(
                name: "ConnectedSystemRunProfiles",
                newName: "ConnectedSystemRunProfile");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemRunProfiles_PartitionId",
                table: "ConnectedSystemRunProfile",
                newName: "IX_ConnectedSystemRunProfile_PartitionId");

            migrationBuilder.RenameIndex(
                name: "IX_ConnectedSystemRunProfiles_ConnectedSystemId",
                table: "ConnectedSystemRunProfile",
                newName: "IX_ConnectedSystemRunProfile_ConnectedSystemId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConnectedSystemRunProfile",
                table: "ConnectedSystemRunProfile",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ConnectedSystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    CheckboxValue = table.Column<bool>(type: "boolean", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StringEncryptedValue = table.Column<string>(type: "text", nullable: true),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemSettings_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSettings_ConnectedSystemId",
                table: "ConnectedSystemSettings",
                column: "ConnectedSystemId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemRunProfile_ConnectedSystemPartitions_Partiti~",
                table: "ConnectedSystemRunProfile",
                column: "PartitionId",
                principalTable: "ConnectedSystemPartitions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemRunProfile_ConnectedSystems_ConnectedSystemId",
                table: "ConnectedSystemRunProfile",
                column: "ConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
