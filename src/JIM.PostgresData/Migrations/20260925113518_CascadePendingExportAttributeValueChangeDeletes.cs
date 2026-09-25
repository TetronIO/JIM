using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class CascadePendingExportAttributeValueChangeDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Historical orphans (#1818): ConnectedSystemRepository.DeletePendingExportAsync deleted the
            // parent Pending Export via EF Remove()+SaveChangesAsync(), and because this relationship carried
            // no explicit OnDelete configuration, EF's default client-side cascade (ClientSetNull) only
            // nulled tracked children's PendingExportId rather than deleting them. Every row this produced
            // is unreachable by any query keyed off a live Pending Export, so it is removed before the
            // foreign key below starts enforcing cascade delete for real.
            migrationBuilder.Sql(@"DELETE FROM ""PendingExportAttributeValueChanges"" WHERE ""PendingExportId"" IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId",
                principalTable: "PendingExports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The orphan deletion in Up() is not reversible: once the rows are gone, there is no record of
            // which Pending Export each one used to belong to. Down only reverts the foreign key's referential
            // action back to NO ACTION; it does not attempt to resurrect the removed rows.
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId",
                principalTable: "PendingExports",
                principalColumn: "Id");
        }
    }
}
