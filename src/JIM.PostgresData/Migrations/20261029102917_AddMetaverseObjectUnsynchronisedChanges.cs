using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaverseObjectUnsynchronisedChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UnsynchronisedChangesJson",
                table: "MetaverseObjects",
                type: "jsonb",
                nullable: true);

            // Flag every internal Metaverse Object for review once (#2615). From this release a Metaverse Object JIM
            // creates or changes outside synchronisation is flagged as it is written, but the ones that already exist
            // (the initial administrator created at first sign-in, Example Data objects) were written before there was
            // anything to flag, and no synchronisation ever visits an internal object, so after the upgrade an export
            // rule that predates one would never provision it. A projected object needs nothing: synchronisation
            // visits it. No unsynchronised change is recorded, because none is known; the review provisions what the
            // object should already have, and a value changed outside synchronisation on an earlier release reaches
            // existing accounts only when it next changes. The cost is bounded: the end-of-run review clears the flags
            // of every type no enabled export rule covers in one statement, without loading the objects. The Internal
            // origin is MetaverseObjectOrigin.Internal (1), written out so the migration does the same thing whenever
            // it runs.
            migrationBuilder.Sql("""
                UPDATE "MetaverseObjects"
                SET "ScopeReviewPending" = true
                WHERE "Origin" = 1
                  AND NOT "ScopeReviewPending";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The flags set by Up are not cleared: a flag on an object that needs no review is cleared by the next
            // review without effect, so leaving them does no harm, and Down cannot tell them from a genuine pending
            // review anyway.
            migrationBuilder.DropColumn(
                name: "UnsynchronisedChangesJson",
                table: "MetaverseObjects");
        }
    }
}
