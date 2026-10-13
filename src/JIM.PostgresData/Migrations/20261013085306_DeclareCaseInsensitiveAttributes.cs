using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// Records which attributes a Connector declares case-insensitive (#2195), and declares them for the LDAP Connected
    /// Systems that already exist.
    /// <para>
    /// The LDAP Connector declares every distinguished name case-insensitive at schema discovery: distinguishedName,
    /// and every DN-syntax attribute, which discovery types as a Reference (AttributeDataType 5). A schema refresh
    /// restates that, but an existing deployment should not need one to stop retrying DNs the directory reports in its
    /// own case, so this applies the same rule once to the attributes already stored. It touches LDAP Connected
    /// Systems only; no other built-in Connector makes the declaration.
    /// </para>
    /// </summary>
    public partial class DeclareCaseInsensitiveAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CaseInsensitive",
                table: "ConnectedSystemAttributes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "ConnectedSystemAttributes" AS attribute
                SET "CaseInsensitive" = TRUE
                FROM "ConnectedSystemObjectTypes" AS object_type
                JOIN "ConnectedSystems" AS connected_system ON connected_system."Id" = object_type."ConnectedSystemId"
                JOIN "ConnectorDefinitions" AS connector ON connector."Id" = connected_system."ConnectorDefinitionId"
                WHERE attribute."ConnectedSystemObjectTypeId" = object_type."Id"
                  AND connector."Name" = 'JIM LDAP Connector'
                  AND (attribute."Type" = 5 OR lower(attribute."Name") = 'distinguishedname');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaseInsensitive",
                table: "ConnectedSystemAttributes");
        }
    }
}
