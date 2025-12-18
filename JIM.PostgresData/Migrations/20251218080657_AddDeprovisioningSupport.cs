using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddDeprovisioningSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ScheduledDeletionDate",
                table: "MetaverseObjects",
                newName: "LastConnectorDisconnectedDate");

            migrationBuilder.AddColumn<int>(
                name: "InboundOutOfScopeAction",
                table: "SyncRules",
                type: "integer",
                nullable: false,
                defaultValue: 1); // 1 = Disconnect (default)

            migrationBuilder.AddColumn<int>(
                name: "OutboundDeprovisionAction",
                table: "SyncRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<int>>(
                name: "DeletionTriggerConnectedSystemIds",
                table: "MetaverseObjectTypes",
                type: "integer[]",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "MetaverseObjects",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InboundOutOfScopeAction",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "OutboundDeprovisionAction",
                table: "SyncRules");

            migrationBuilder.DropColumn(
                name: "DeletionTriggerConnectedSystemIds",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "MetaverseObjects");

            migrationBuilder.RenameColumn(
                name: "LastConnectorDisconnectedDate",
                table: "MetaverseObjects",
                newName: "ScheduledDeletionDate");
        }
    }
}
