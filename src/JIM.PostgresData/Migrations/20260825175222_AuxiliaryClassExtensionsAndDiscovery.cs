using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AuxiliaryClassExtensionsAndDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuxiliaryClassDiscoveryWorkerTask_ConnectedSystemId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SampleSizePerObjectType",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StructuralCarrierObjectTypeId",
                table: "ConnectedSystemObjectTypes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Required",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AuxiliaryClassDiscoveryRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    SampleSizePerObjectType = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Started = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Completed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EntriesRead = table.Column<int>(type: "integer", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatedByName = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuxiliaryClassDiscoveryRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuxiliaryClassDiscoveryRuns_ConnectedSystems_ConnectedSyste~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjectTypeExtensions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    ExtensionObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjectTypeExtensions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectTypeExtensions_ConnectedSystemObjectTy~",
                        column: x => x.BaseObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectTypeExtensions_ConnectedSystemObjectT~1",
                        column: x => x.ExtensionObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuxiliaryClassDiscoveryResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<int>(type: "integer", nullable: false),
                    StructuralObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    AuxiliaryClassName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EntryCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuxiliaryClassDiscoveryResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuxiliaryClassDiscoveryResults_AuxiliaryClassDiscoveryRuns_~",
                        column: x => x.RunId,
                        principalTable: "AuxiliaryClassDiscoveryRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AuxiliaryClassDiscoveryResults_ConnectedSystemObjectTypes_S~",
                        column: x => x.StructuralObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectTypes_StructuralCarrierObjectTypeId",
                table: "ConnectedSystemObjectTypes",
                column: "StructuralCarrierObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AuxiliaryClassDiscoveryResults_RunId_StructuralObjectTypeId~",
                table: "AuxiliaryClassDiscoveryResults",
                columns: new[] { "RunId", "StructuralObjectTypeId", "AuxiliaryClassName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuxiliaryClassDiscoveryResults_StructuralObjectTypeId",
                table: "AuxiliaryClassDiscoveryResults",
                column: "StructuralObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AuxiliaryClassDiscoveryRuns_ConnectedSystemId",
                table: "AuxiliaryClassDiscoveryRuns",
                column: "ConnectedSystemId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectTypeExtensions_BaseObjectTypeId_Extens~",
                table: "ConnectedSystemObjectTypeExtensions",
                columns: new[] { "BaseObjectTypeId", "ExtensionObjectTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectTypeExtensions_ExtensionObjectTypeId",
                table: "ConnectedSystemObjectTypeExtensions",
                column: "ExtensionObjectTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectTypes_ConnectedSystemObjectTypes_Struc~",
                table: "ConnectedSystemObjectTypes",
                column: "StructuralCarrierObjectTypeId",
                principalTable: "ConnectedSystemObjectTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectTypes_ConnectedSystemObjectTypes_Struc~",
                table: "ConnectedSystemObjectTypes");

            migrationBuilder.DropTable(
                name: "AuxiliaryClassDiscoveryResults");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectTypeExtensions");

            migrationBuilder.DropTable(
                name: "AuxiliaryClassDiscoveryRuns");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectTypes_StructuralCarrierObjectTypeId",
                table: "ConnectedSystemObjectTypes");

            migrationBuilder.DropColumn(
                name: "AuxiliaryClassDiscoveryWorkerTask_ConnectedSystemId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "SampleSizePerObjectType",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "StructuralCarrierObjectTypeId",
                table: "ConnectedSystemObjectTypes");

            migrationBuilder.DropColumn(
                name: "Required",
                table: "ConnectedSystemAttributes");
        }
    }
}
