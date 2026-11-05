using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemManagesExchangeRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ManagesExchangeRecipients",
                table: "ConnectedSystems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // A Connected System saved before this column existed manages Exchange recipients when its LDAP Connector's
            // setting says so; every later save records it from the Connector (#2021).
            migrationBuilder.Sql("""
                UPDATE "ConnectedSystems" cs
                SET "ManagesExchangeRecipients" = true
                FROM "ConnectedSystemSettingValues" v
                JOIN "ConnectorDefinitionSettings" s ON s."Id" = v."SettingId"
                WHERE v."ConnectedSystemId" = cs."Id"
                  AND s."Name" = 'Manage Exchange recipients'
                  AND v."CheckboxValue" = true;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManagesExchangeRecipients",
                table: "ConnectedSystems");
        }
    }
}
