using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RequireSyncRuleMappingOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove any orphaned mapping rows (and their sources) before the column refuses them (#1550).
            // Orphans were produced by the portal's staged-removal save severing the relationship, which
            // nulled the owner rather than deleting the row; nothing can reference them (every read is keyed
            // on SyncRuleId), so deleting them is the backfill.
            migrationBuilder.Sql("""
                DELETE FROM "SyncRuleMappingSources"
                WHERE "SyncRuleMappingId" IN (SELECT "Id" FROM "SyncRuleMappings" WHERE "SyncRuleId" IS NULL);
                DELETE FROM "SyncRuleMappings" WHERE "SyncRuleId" IS NULL;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "SyncRuleId",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SyncRuleId",
                table: "SyncRuleMappings",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");
        }
    }
}
