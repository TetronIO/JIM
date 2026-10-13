using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class UnifyCriterionOperators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #2053: Example Data dependencies move from their own ComparisonType enum to the operator catalogue's
            // SearchComparisonType. One statement, so no row is remapped twice. "Like" never worked (it threw at
            // generation time) and becomes Contains, its nearest meaning.
            migrationBuilder.Sql("""
                UPDATE "ExampleDataTemplateAttributeDependencies"
                SET "ComparisonType" = CASE "ComparisonType"
                    WHEN 0 THEN 1   -- Equals
                    WHEN 1 THEN 2   -- NotEquals
                    WHEN 2 THEN 9   -- LessThan
                    WHEN 3 THEN 11  -- GreaterThan
                    WHEN 4 THEN 12  -- GreaterThanOrEqual -> GreaterThanOrEquals
                    WHEN 5 THEN 10  -- LessThanOrEqual -> LessThanOrEquals
                    WHEN 6 THEN 7   -- Like -> Contains
                    ELSE "ComparisonType"
                END;
                """);

            // #2053: "Equals with no value" was the only way to scope on a missing value; it becomes "has no value"
            // (14), which means exactly the same for every data type. "Does not equal with no value" meant "has a value"
            // (13) for Text, Boolean and Guid, and is rewritten for those only: on a number or a date it never matched
            // anything, so rewriting it would bring objects into scope, and it is left as it is.
            migrationBuilder.Sql("""
                UPDATE "SyncRuleScopingCriteria"
                SET "ComparisonType" = 14
                WHERE "ComparisonType" = 1
                  AND "ValueMode" = 0
                  AND "StringValue" IS NULL AND "IntValue" IS NULL AND "LongValue" IS NULL AND "DecimalValue" IS NULL
                  AND "DateTimeValue" IS NULL AND "BoolValue" IS NULL AND "GuidValue" IS NULL;

                UPDATE "SyncRuleScopingCriteria" c
                SET "ComparisonType" = 13
                WHERE c."ComparisonType" = 2
                  AND c."ValueMode" = 0
                  AND c."StringValue" IS NULL AND c."IntValue" IS NULL AND c."LongValue" IS NULL AND c."DecimalValue" IS NULL
                  AND c."DateTimeValue" IS NULL AND c."BoolValue" IS NULL AND c."GuidValue" IS NULL
                  AND COALESCE(
                        (SELECT ma."Type" FROM "MetaverseAttributes" ma WHERE ma."Id" = c."MetaverseAttributeId"),
                        (SELECT ca."Type" FROM "ConnectedSystemAttributes" ca WHERE ca."Id" = c."ConnectedSystemAttributeId")
                      ) IN (1, 6, 7); -- Text, Guid, Boolean
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to the workaround the presence operators replaced.
            migrationBuilder.Sql("""
                UPDATE "SyncRuleScopingCriteria" SET "ComparisonType" = 1 WHERE "ComparisonType" = 14;
                UPDATE "SyncRuleScopingCriteria" SET "ComparisonType" = 2 WHERE "ComparisonType" = 13;
                """);

            // Operators the old enum never had fall back to the nearest one it did: the substring family to Like, and
            // everything else to Equals. The old enum could not evaluate either, so nothing that worked is lost.
            migrationBuilder.Sql("""
                UPDATE "ExampleDataTemplateAttributeDependencies"
                SET "ComparisonType" = CASE "ComparisonType"
                    WHEN 1 THEN 0
                    WHEN 2 THEN 1
                    WHEN 9 THEN 2
                    WHEN 11 THEN 3
                    WHEN 12 THEN 4
                    WHEN 10 THEN 5
                    WHEN 3 THEN 6 WHEN 4 THEN 6 WHEN 5 THEN 6 WHEN 6 THEN 6 WHEN 7 THEN 6 WHEN 8 THEN 6
                    ELSE 0
                END;
                """);
        }
    }
}
