using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// Removes credential values JIM already holds for attributes on the credential denylist (#2091). Before this
    /// release <c>password</c> was not denied, so an Attribute Flow could stage a cleartext password into a Pending
    /// Export, and the export could record it on the Connected System Object. The sync engine no longer stages these,
    /// but values already persisted would otherwise sit in the database indefinitely.
    /// <para>
    /// Deletes Pending Export attribute value changes targeting a denied attribute, any update-type Pending Export
    /// that held nothing else, and Connected System Object attribute values for denied attributes. The attribute definitions
    /// themselves stay: schema refresh quarantines them, and Synchronisation Rule validation requires any Attribute
    /// Flow using one to be removed. Change history is left alone; the documentation describes clearing it.
    /// </para>
    /// <para>
    /// The names are the denylist as it stands in this release, written out rather than read from
    /// <c>CredentialAttributes</c>, so the migration does the same thing whenever it runs. Matching ignores case, as
    /// the denylist does.
    /// </para>
    /// </summary>
    public partial class PurgeCredentialAttributeValues : Migration
    {
        private const string DeniedAttributeIds = """
            SELECT "Id" FROM "ConnectedSystemAttributes"
            WHERE lower("Name") IN (
                'unicodepwd', 'userpassword', 'dbcspwd', 'ntpwdhistory', 'lmpwdhistory', 'supplementalcredentials',
                'unixuserpassword', 'msds-managedpassword', 'pwdhistory', 'passwordhistory', 'password')
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One statement, so the Pending Exports it removes are exactly those whose every change was a denied
            // attribute's. ChangeType 1 is Update: a Create left with no attribute changes still creates the object,
            // and a Delete carries none to begin with. An update that never held a denied change is not touched, even
            // if it has no attribute changes for some other reason.
            migrationBuilder.Sql($"""
                WITH removed AS (
                    DELETE FROM "PendingExportAttributeValueChanges"
                    WHERE "AttributeId" IN ({DeniedAttributeIds})
                    RETURNING "PendingExportId"
                )
                DELETE FROM "PendingExports" pe
                WHERE pe."ChangeType" = 1
                  AND pe."Id" IN (SELECT "PendingExportId" FROM removed)
                  AND NOT EXISTS (
                      SELECT 1 FROM "PendingExportAttributeValueChanges" c
                      WHERE c."PendingExportId" = pe."Id"
                        AND c."AttributeId" NOT IN ({DeniedAttributeIds}));
                """);

            migrationBuilder.Sql($"""
                DELETE FROM "ConnectedSystemObjectAttributeValues"
                WHERE "AttributeId" IN ({DeniedAttributeIds});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted credential values are not restored.
        }
    }
}
