using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddScimStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScimStoreJournalEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceTypeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Operation = table.Column<int>(type: "integer", nullable: false),
                    ChangedAttributes = table.Column<List<string>>(type: "text[]", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApiKeyName = table.Column<string>(type: "text", nullable: true),
                    ClientAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Occurred = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreJournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScimStoreJournalEntries_ApiKeys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ScimStoreJournalEntries_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimStoreResourceTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreResourceTypes", x => x.Id);
                    table.UniqueConstraint("AK_ScimStoreResourceTypes_ConnectedSystemId_Id", x => new { x.ConnectedSystemId, x.Id });
                    table.ForeignKey(
                        name: "FK_ScimStoreResourceTypes_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimStoreSchemas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Urn = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreSchemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScimStoreSchemas_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimStoreStates",
                columns: table => new
                {
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    LastChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    TombstonePurgeFloor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreStates", x => x.ConnectedSystemId);
                    table.ForeignKey(
                        name: "FK_ScimStoreStates_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimStoreResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ResourceTypeId = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreResources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScimStoreResources_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScimStoreResources_ScimStoreResourceTypes_ConnectedSystemId~",
                        columns: x => new { x.ConnectedSystemId, x.ResourceTypeId },
                        principalTable: "ScimStoreResourceTypes",
                        principalColumns: new[] { "ConnectedSystemId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "ScimStoreReferences",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attribute = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreReferences", x => new { x.SourceId, x.Attribute, x.TargetId });
                    table.ForeignKey(
                        name: "FK_ScimStoreReferences_ScimStoreResources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "ScimStoreResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScimStoreReferences_ScimStoreResources_TargetId",
                        column: x => x.TargetId,
                        principalTable: "ScimStoreResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimStoreUniqueValues",
                columns: table => new
                {
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ResourceTypeId = table.Column<int>(type: "integer", nullable: false),
                    Attribute = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    ValueHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreUniqueValues", x => new { x.ConnectedSystemId, x.ResourceTypeId, x.Attribute, x.ValueHash });
                    table.ForeignKey(
                        name: "FK_ScimStoreUniqueValues_ScimStoreResources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "ScimStoreResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreJournalEntries_ApiKeyId",
                table: "ScimStoreJournalEntries",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreJournalEntries_ConnectedSystemId_ResourceId_Change~",
                table: "ScimStoreJournalEntries",
                columns: new[] { "ConnectedSystemId", "ResourceId", "ChangeSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreJournalEntries_Occurred",
                table: "ScimStoreJournalEntries",
                column: "Occurred");

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreReferences_TargetId",
                table: "ScimStoreReferences",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreResources_ChangeSequence",
                table: "ScimStoreResources",
                columns: new[] { "ConnectedSystemId", "ResourceTypeId", "ChangeSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreResources_Live",
                table: "ScimStoreResources",
                columns: new[] { "ConnectedSystemId", "ResourceTypeId", "Id" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreResourceTypes_ConnectedSystemId_Endpoint",
                table: "ScimStoreResourceTypes",
                columns: new[] { "ConnectedSystemId", "Endpoint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreResourceTypes_ConnectedSystemId_Name",
                table: "ScimStoreResourceTypes",
                columns: new[] { "ConnectedSystemId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreSchemas_ConnectedSystemId_Urn",
                table: "ScimStoreSchemas",
                columns: new[] { "ConnectedSystemId", "Urn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreUniqueValues_ResourceId",
                table: "ScimStoreUniqueValues",
                column: "ResourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScimStoreJournalEntries");

            migrationBuilder.DropTable(
                name: "ScimStoreReferences");

            migrationBuilder.DropTable(
                name: "ScimStoreSchemas");

            migrationBuilder.DropTable(
                name: "ScimStoreStates");

            migrationBuilder.DropTable(
                name: "ScimStoreUniqueValues");

            migrationBuilder.DropTable(
                name: "ScimStoreResources");

            migrationBuilder.DropTable(
                name: "ScimStoreResourceTypes");
        }
    }
}
