using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ChangeHistoryAttributesWithoutForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A change record's attribute rows name their attribute by plain id from now on (#2931), as change records name
            // their object (#2669): deleting an attribute no longer rewrites the history naming it. Ids an earlier deletion
            // cleared stay null; the copied name and type are what those rows are read by. The AttributeId indexes stay.
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemAttrib~",
                table: "ConnectedSystemObjectChangeAttributes");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChangeAttributes_MetaverseAttributes_Attribu~",
                table: "MetaverseObjectChangeAttributes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The constraints refuse an id with no attribute behind it, which history now keeps; clear those first, as the
            // deletions did before this migration.
            migrationBuilder.Sql(
                @"UPDATE ""ConnectedSystemObjectChangeAttributes"" ca SET ""AttributeId"" = NULL
                  WHERE ""AttributeId"" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ""ConnectedSystemAttributes"" a WHERE a.""Id"" = ca.""AttributeId"");");
            migrationBuilder.Sql(
                @"UPDATE ""MetaverseObjectChangeAttributes"" ca SET ""AttributeId"" = NULL
                  WHERE ""AttributeId"" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ""MetaverseAttributes"" a WHERE a.""Id"" = ca.""AttributeId"");");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemAttrib~",
                table: "ConnectedSystemObjectChangeAttributes",
                column: "AttributeId",
                principalTable: "ConnectedSystemAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChangeAttributes_MetaverseAttributes_Attribu~",
                table: "MetaverseObjectChangeAttributes",
                column: "AttributeId",
                principalTable: "MetaverseAttributes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
