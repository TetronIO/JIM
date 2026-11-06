using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AuditReferencesWithoutForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystem~1",
                table: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChangeAttributeValues_MetaverseObjects_Refer~",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_MetaverseObjects_MetaverseObjectId",
                table: "MetaverseObjectChanges");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChangeAttributeValues_ReferenceValueId",
                table: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectChangeAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectChangeAttributeValues");

            // History keeps its subject id from now on (#2669). Restore it on the records earlier deletions took it from:
            // a deleted Metaverse Object's records were re-keyed to DeletedMetaverseObjectId, and a Connected System Object's
            // deletion record was written with the id there instead. After the constraints are dropped, since the ids name
            // objects that no longer exist. Records that held neither id lost it for good and are left as they are.
            migrationBuilder.Sql(
                @"UPDATE ""MetaverseObjectChanges"" SET ""MetaverseObjectId"" = ""DeletedMetaverseObjectId""
                  WHERE ""MetaverseObjectId"" IS NULL AND ""DeletedMetaverseObjectId"" IS NOT NULL;");
            migrationBuilder.Sql(
                @"UPDATE ""ConnectedSystemObjectChanges"" SET ""ConnectedSystemObjectId"" = ""DeletedConnectedSystemObjectId""
                  WHERE ""ConnectedSystemObjectId"" IS NULL AND ""DeletedConnectedSystemObjectId"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The constraints refuse an id with no object behind it, which history now keeps; clear those first, as the
            // deletions did before this migration. The subject ids restored on deletion records are left; the
            // Deleted*ObjectId columns still hold them.
            migrationBuilder.Sql(
                @"UPDATE ""ActivityRunProfileExecutionItems"" i SET ""ConnectedSystemObjectId"" = NULL
                  WHERE ""ConnectedSystemObjectId"" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ""ConnectedSystemObjects"" o WHERE o.""Id"" = i.""ConnectedSystemObjectId"");");
            migrationBuilder.Sql(
                @"UPDATE ""ConnectedSystemObjectChanges"" c SET ""ConnectedSystemObjectId"" = NULL
                  WHERE ""ConnectedSystemObjectId"" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ""ConnectedSystemObjects"" o WHERE o.""Id"" = c.""ConnectedSystemObjectId"");");
            migrationBuilder.Sql(
                @"UPDATE ""ConnectedSystemObjectChangeAttributeValues"" v SET ""ReferenceValueId"" = NULL
                  WHERE ""ReferenceValueId"" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ""ConnectedSystemObjects"" o WHERE o.""Id"" = v.""ReferenceValueId"");");
            migrationBuilder.Sql(
                @"UPDATE ""MetaverseObjectChanges"" c SET ""MetaverseObjectId"" = NULL
                  WHERE ""MetaverseObjectId"" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ""MetaverseObjects"" o WHERE o.""Id"" = c.""MetaverseObjectId"");");
            migrationBuilder.Sql(
                @"UPDATE ""MetaverseObjectChangeAttributeValues"" v SET ""ReferenceValueId"" = NULL
                  WHERE ""ReferenceValueId"" IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM ""MetaverseObjects"" o WHERE o.""Id"" = v.""ReferenceValueId"");");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChangeAttributeValues_ReferenceValueId",
                table: "MetaverseObjectChangeAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChangeAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectChangeAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                table: "ActivityRunProfileExecutionItems",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystem~1",
                table: "ConnectedSystemObjectChangeAttributeValues",
                column: "ReferenceValueId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChangeAttributeValues_MetaverseObjects_Refer~",
                table: "MetaverseObjectChangeAttributeValues",
                column: "ReferenceValueId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_MetaverseObjects_MetaverseObjectId",
                table: "MetaverseObjectChanges",
                column: "MetaverseObjectId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }
    }
}
