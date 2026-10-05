using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddRetiredGeneratedValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RetiredGeneratedValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemObjectTypeAttributeId = table.Column<int>(type: "integer", nullable: true),
                    Value = table.Column<string>(type: "text", nullable: false),
                    NormalisedValue = table.Column<string>(type: "text", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    FromObjectDisplayName = table.Column<string>(type: "text", nullable: true),
                    FromObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetiredGeneratedValues", x => x.Id);
                    table.CheckConstraint("CK_RetiredGeneratedValues_OneAttribute", "(\"MetaverseAttributeId\" IS NOT NULL)::int + (\"ConnectedSystemObjectTypeAttributeId\" IS NOT NULL)::int = 1");
                    table.ForeignKey(
                        name: "FK_RetiredGeneratedValues_ConnectedSystemAttributes_ConnectedS~",
                        column: x => x.ConnectedSystemObjectTypeAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RetiredGeneratedValues_MetaverseAttributes_MetaverseAttribu~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RetiredGeneratedValues_ConnectedSystemObjectTypeAttributeId",
                table: "RetiredGeneratedValues",
                column: "ConnectedSystemObjectTypeAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_RetiredGeneratedValues_FromObjectId",
                table: "RetiredGeneratedValues",
                column: "FromObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RetiredGeneratedValues_MetaverseAttributeId",
                table: "RetiredGeneratedValues",
                column: "MetaverseAttributeId");

            // Unique Value Generation (#242, Phase 6): a value is retired at most once per attribute, compared
            // case-insensitively (FR 31). An expression index, which EF cannot model, so it is raw SQL here and named
            // in JimDbContext's comment beside the entity. The retired gate's lookup uses exactly this expression.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_RetiredGeneratedValues_MvAttributeId_LowerNormalisedValue_Unique"
                ON "RetiredGeneratedValues" ("MetaverseAttributeId", LOWER("NormalisedValue"))
                WHERE "MetaverseAttributeId" IS NOT NULL;
                """);
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_RetiredGeneratedValues_CsAttributeId_LowerNormalisedValue_Unique"
                ON "RetiredGeneratedValues" ("ConnectedSystemObjectTypeAttributeId", LOWER("NormalisedValue"))
                WHERE "ConnectedSystemObjectTypeAttributeId" IS NOT NULL;
                """);

            // A generated flow that is removed retires the values it issued (plan "Assignment lifecycle" row 2),
            // reason Recalled (3). A flow goes through many paths: deleting the mapping, its Synchronisation Rule or
            // its Connected System, a whole-rule save that drops it or changes its source type, unassigning its
            // target attribute. Every one of them ends in this table's row being deleted, directly or by cascade, so
            // the retirement is written here, before the row and (by its cascade) its assignments go, in the same
            // statement and transaction. Only flows that never reuse values write anything (NeverReuse on, or a
            // Sequence token, TokenKind 1); a value already retired is left as it is. TRUNCATE (the factory reset)
            // fires no DELETE trigger, which is correct: the reset clears the register itself.
            //
            // The column list is RetiredGeneratedValueBulkColumns.RetiredGeneratedValues, written out because a
            // trigger cannot read a C# constant; RetiredGeneratedValueRegisterDatabaseTests asserts this function
            // names every column in that list. The Connected System Object name follows ObjectNaming's candidates.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jim_retire_generated_values_on_generation_delete() RETURNS trigger AS $$
                BEGIN
                    IF OLD."NeverReuse" OR OLD."TokenKind" = 1 THEN
                        INSERT INTO "RetiredGeneratedValues"
                            ("MetaverseAttributeId", "ConnectedSystemObjectTypeAttributeId", "Value", "NormalisedValue",
                             "RetiredAt", "Reason", "FromObjectDisplayName", "FromObjectId", "ActivityId")
                        SELECT a."MetaverseAttributeId",
                               a."ConnectedSystemObjectTypeAttributeId",
                               a."Value",
                               LOWER(a."NormalisedValue"),
                               now(),
                               3,
                               CASE WHEN a."MetaverseObjectId" IS NOT NULL THEN mvo."CachedDisplayName"
                                    ELSE (SELECT v."StringValue"
                                          FROM "ConnectedSystemObjectAttributeValues" v
                                          INNER JOIN "ConnectedSystemAttributes" ca ON ca."Id" = v."AttributeId"
                                          WHERE v."ConnectedSystemObjectId" = a."ConnectedSystemObjectId"
                                            AND LOWER(ca."Name") IN ('displayname', 'cn', 'name')
                                            AND BTRIM(COALESCE(v."StringValue", '')) <> ''
                                          ORDER BY CASE LOWER(ca."Name") WHEN 'displayname' THEN 0 WHEN 'cn' THEN 1 ELSE 2 END
                                          LIMIT 1) END,
                               COALESCE(a."MetaverseObjectId", a."ConnectedSystemObjectId"),
                               NULL
                        FROM "GeneratedValueAssignments" a
                        LEFT JOIN "MetaverseObjects" mvo ON mvo."Id" = a."MetaverseObjectId"
                        WHERE a."SyncRuleMappingGenerationId" = OLD."Id"
                        ON CONFLICT DO NOTHING;
                    END IF;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_sync_rule_mapping_generations_retire_values
                BEFORE DELETE ON "SyncRuleMappingGenerations"
                FOR EACH ROW EXECUTE FUNCTION jim_retire_generated_values_on_generation_delete();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_sync_rule_mapping_generations_retire_values ON "SyncRuleMappingGenerations";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jim_retire_generated_values_on_generation_delete();");

            migrationBuilder.DropTable(
                name: "RetiredGeneratedValues");
        }
    }
}
