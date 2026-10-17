using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ClearRunProfileFilePathOutsideImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Run Profile's File Path only chooses the file an import reads; an export always writes to the Connected
            // System's File Path, so a path stored on any other Run Profile did nothing. Clear it, or the next save of
            // that Run Profile would be refused. ConnectedSystemRunType: 1 = FullImport, 2 = DeltaImport.
            migrationBuilder.Sql("""
                UPDATE "ConnectedSystemRunProfiles"
                SET "FilePath" = NULL
                WHERE "FilePath" IS NOT NULL
                  AND "RunType" NOT IN (1, 2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The cleared paths were never read, so there is nothing to restore.
        }
    }
}
