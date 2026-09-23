using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueValueGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GeneratedValueSequences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemObjectTypeAttributeId = table.Column<int>(type: "integer", nullable: true),
                    NextValue = table.Column<long>(type: "bigint", nullable: false),
                    AssignedCount = table.Column<long>(type: "bigint", nullable: false),
                    LastMovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastMovedBySyncRuleMappingId = table.Column<int>(type: "integer", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedValueSequences", x => x.Id);
                    table.CheckConstraint("CK_GeneratedValueSequences_OneAttribute", "(\"MetaverseAttributeId\" IS NOT NULL)::int + (\"ConnectedSystemObjectTypeAttributeId\" IS NOT NULL)::int = 1");
                    table.ForeignKey(
                        name: "FK_GeneratedValueSequences_ConnectedSystemAttributes_Connected~",
                        column: x => x.ConnectedSystemObjectTypeAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedValueSequences_MetaverseAttributes_MetaverseAttrib~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedValueSequences_SyncRuleMappings_LastMovedBySyncRul~",
                        column: x => x.LastMovedBySyncRuleMappingId,
                        principalTable: "SyncRuleMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMappingGenerations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SyncRuleMappingId = table.Column<int>(type: "integer", nullable: false),
                    TokenKind = table.Column<int>(type: "integer", nullable: false),
                    SuffixStyle = table.Column<int>(type: "integer", nullable: false),
                    SuffixStart = table.Column<int>(type: "integer", nullable: false),
                    SequenceStart = table.Column<long>(type: "bigint", nullable: false),
                    SequenceIncrement = table.Column<int>(type: "integer", nullable: false),
                    FixedWidth = table.Column<int>(type: "integer", nullable: true),
                    OnWidthExceeded = table.Column<int>(type: "integer", nullable: false),
                    RandomFormat = table.Column<int>(type: "integer", nullable: false),
                    RandomLength = table.Column<int>(type: "integer", nullable: true),
                    Separator = table.Column<string>(type: "text", nullable: true),
                    AttemptLimit = table.Column<int>(type: "integer", nullable: false),
                    NeverReuse = table.Column<bool>(type: "boolean", nullable: false),
                    CollisionRemediation = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappingGenerations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingGenerations_SyncRuleMappings_SyncRuleMapping~",
                        column: x => x.SyncRuleMappingId,
                        principalTable: "SyncRuleMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedValueAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemObjectTypeAttributeId = table.Column<int>(type: "integer", nullable: true),
                    Value = table.Column<string>(type: "text", nullable: false),
                    NormalisedValue = table.Column<string>(type: "text", nullable: false),
                    PreviousValue = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SyncRuleMappingGenerationId = table.Column<int>(type: "integer", nullable: false),
                    Adopted = table.Column<bool>(type: "boolean", nullable: false),
                    RemediationCount = table.Column<int>(type: "integer", nullable: false),
                    RenameAuthorised = table.Column<bool>(type: "boolean", nullable: false),
                    RenameAuthorisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenameAuthorisedByName = table.Column<string>(type: "text", nullable: true),
                    RejectedByConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    AnchoredByConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    RemediatedByActivityRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    NeedsDecisionEnteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NeedsDecisionActivityRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedValueAssignments", x => x.Id);
                    table.CheckConstraint("CK_GeneratedValueAssignments_OneMode", "(\"MetaverseObjectId\" IS NOT NULL AND \"MetaverseAttributeId\" IS NOT NULL AND \"ConnectedSystemObjectId\" IS NULL AND \"ConnectedSystemObjectTypeAttributeId\" IS NULL) OR (\"ConnectedSystemObjectId\" IS NOT NULL AND \"ConnectedSystemObjectTypeAttributeId\" IS NOT NULL AND \"MetaverseObjectId\" IS NULL AND \"MetaverseAttributeId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_GeneratedValueAssignments_ConnectedSystemAttributes_Connect~",
                        column: x => x.ConnectedSystemObjectTypeAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedValueAssignments_ConnectedSystemObjects_ConnectedS~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedValueAssignments_MetaverseAttributes_MetaverseAttr~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedValueAssignments_MetaverseObjects_MetaverseObjectId",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GeneratedValueAssignments_SyncRuleMappingGenerations_SyncRu~",
                        column: x => x.SyncRuleMappingGenerationId,
                        principalTable: "SyncRuleMappingGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMappingGenerationExclusions",
                columns: table => new
                {
                    SyncRuleMappingGenerationId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappingGenerationExclusions", x => new { x.SyncRuleMappingGenerationId, x.ConnectedSystemId });
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingGenerationExclusions_ConnectedSystems_Connec~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingGenerationExclusions_SyncRuleMappingGenerati~",
                        column: x => x.SyncRuleMappingGenerationId,
                        principalTable: "SyncRuleMappingGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_CsAttributeId_NormalisedValue_Unique",
                table: "GeneratedValueAssignments",
                columns: new[] { "ConnectedSystemObjectTypeAttributeId", "NormalisedValue" },
                unique: true,
                filter: "\"ConnectedSystemObjectTypeAttributeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_CsoId_AttributeId_Unique",
                table: "GeneratedValueAssignments",
                columns: new[] { "ConnectedSystemObjectId", "ConnectedSystemObjectTypeAttributeId" },
                unique: true,
                filter: "\"ConnectedSystemObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_MvAttributeId_NormalisedValue_Unique",
                table: "GeneratedValueAssignments",
                columns: new[] { "MetaverseAttributeId", "NormalisedValue" },
                unique: true,
                filter: "\"MetaverseAttributeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_MvoId_AttributeId_Unique",
                table: "GeneratedValueAssignments",
                columns: new[] { "MetaverseObjectId", "MetaverseAttributeId" },
                unique: true,
                filter: "\"MetaverseObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_State",
                table: "GeneratedValueAssignments",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueAssignments_SyncRuleMappingGenerationId",
                table: "GeneratedValueAssignments",
                column: "SyncRuleMappingGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueSequences_ConnectedSystemObjectTypeAttributeId_Unique",
                table: "GeneratedValueSequences",
                column: "ConnectedSystemObjectTypeAttributeId",
                unique: true,
                filter: "\"ConnectedSystemObjectTypeAttributeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueSequences_LastMovedBySyncRuleMappingId",
                table: "GeneratedValueSequences",
                column: "LastMovedBySyncRuleMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedValueSequences_MetaverseAttributeId_Unique",
                table: "GeneratedValueSequences",
                column: "MetaverseAttributeId",
                unique: true,
                filter: "\"MetaverseAttributeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingGenerationExclusions_ConnectedSystemId",
                table: "SyncRuleMappingGenerationExclusions",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingGenerations_SyncRuleMappingId_Unique",
                table: "SyncRuleMappingGenerations",
                column: "SyncRuleMappingId",
                unique: true);

            // Case-insensitive lookups for the unique value gates (#242, plan decision 13). EF Core cannot model an
            // expression column, so the two LOWER("StringValue") indexes live here as raw SQL; the gates query
            // LOWER("StringValue") = ANY(@values) against them. Filtered like the existing (AttributeId, StringValue)
            // indexes so null values take no space.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_MetaverseObjectAttributeValues_AttributeId_LowerStringValue\" " +
                "ON \"MetaverseObjectAttributeValues\" (\"AttributeId\", LOWER(\"StringValue\")) " +
                "WHERE \"StringValue\" IS NOT NULL;");

            migrationBuilder.Sql(
                "CREATE INDEX \"IX_ConnectedSystemObjectAttributeValues_AttributeId_LowerStringValue\" " +
                "ON \"ConnectedSystemObjectAttributeValues\" (\"AttributeId\", LOWER(\"StringValue\")) " +
                "WHERE \"StringValue\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_ConnectedSystemObjectAttributeValues_AttributeId_LowerStringValue\";");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_MetaverseObjectAttributeValues_AttributeId_LowerStringValue\";");
            migrationBuilder.DropTable(
                name: "GeneratedValueAssignments");

            migrationBuilder.DropTable(
                name: "GeneratedValueSequences");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingGenerationExclusions");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingGenerations");
        }
    }
}
