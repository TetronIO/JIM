using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedDisplayNameToMetaverseObject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CachedDisplayName",
                table: "MetaverseObjects",
                type: "text",
                nullable: true);

            // Backfill from existing Display Name attribute values
            migrationBuilder.Sql("""
                UPDATE "MetaverseObjects" m
                SET "CachedDisplayName" = (
                    SELECT av."StringValue"
                    FROM "MetaverseObjectAttributeValues" av
                    INNER JOIN "MetaverseAttributes" ma ON av."AttributeId" = ma."Id"
                    WHERE av."MetaverseObjectId" = m."Id"
                      AND ma."Name" = 'Display Name'
                      AND av."StringValue" IS NOT NULL
                    LIMIT 1
                )
                """);

            // Composite index for the exact WHERE + ORDER BY pattern used by
            // GetMetaverseObjectHeadersPagedAsync: WHERE TypeId = @typeId ORDER BY CachedDisplayName
            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjects_TypeId_CachedDisplayName",
                table: "MetaverseObjects",
                columns: new[] { "TypeId", "CachedDisplayName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjects_TypeId_CachedDisplayName",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "CachedDisplayName",
                table: "MetaverseObjects");
        }
    }
}
