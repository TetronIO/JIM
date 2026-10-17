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
                    ResourceType = table.Column<int>(type: "integer", nullable: false),
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
                name: "ScimStoreResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ResourceType = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    UserNameNormalised = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true, computedColumnSql: "lower(\"UserName\")", stored: true)
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
                name: "ScimStoreGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimStoreGroupMembers", x => new { x.GroupId, x.MemberId });
                    table.ForeignKey(
                        name: "FK_ScimStoreGroupMembers_ScimStoreResources_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ScimStoreResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScimStoreGroupMembers_ScimStoreResources_MemberId",
                        column: x => x.MemberId,
                        principalTable: "ScimStoreResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreGroupMembers_MemberId",
                table: "ScimStoreGroupMembers",
                column: "MemberId");

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
                name: "IX_ScimStoreResources_ChangeSequence",
                table: "ScimStoreResources",
                columns: new[] { "ConnectedSystemId", "ResourceType", "ChangeSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreResources_ExternalId_Unique",
                table: "ScimStoreResources",
                columns: new[] { "ConnectedSystemId", "ResourceType", "ExternalId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreResources_Live",
                table: "ScimStoreResources",
                columns: new[] { "ConnectedSystemId", "ResourceType", "Id" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScimStoreResources_UserName_Unique",
                table: "ScimStoreResources",
                columns: new[] { "ConnectedSystemId", "ResourceType", "UserNameNormalised" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"UserNameNormalised\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScimStoreGroupMembers");

            migrationBuilder.DropTable(
                name: "ScimStoreJournalEntries");

            migrationBuilder.DropTable(
                name: "ScimStoreStates");

            migrationBuilder.DropTable(
                name: "ScimStoreResources");
        }
    }
}
