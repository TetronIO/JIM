using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingPasswordChangeCarriedObligation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProvisionedConnectedSystemObjectId",
                table: "PendingPasswordChanges",
                type: "uuid",
                nullable: true);

            // An Initial Password already queued owes its own account its first password (#2160): give it the mark the
            // delivery and the leaver's export now read, so an upgrade mid-delivery changes nothing for it.
            // Provisioned = 2 (PendingPasswordChangeOrigin).
            migrationBuilder.Sql("""
                UPDATE "PendingPasswordChanges"
                SET "ProvisionedConnectedSystemObjectId" = "ConnectedSystemObjectId"
                WHERE "Origin" = 2 AND "ConnectedSystemObjectId" IS NOT NULL
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PendingPasswordChanges_ProvisionedConnectedSystemObjectId",
                table: "PendingPasswordChanges",
                column: "ProvisionedConnectedSystemObjectId",
                filter: "\"ProvisionedConnectedSystemObjectId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingPasswordChanges_ConnectedSystemObjects_ProvisionedCo~",
                table: "PendingPasswordChanges",
                column: "ProvisionedConnectedSystemObjectId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingPasswordChanges_ConnectedSystemObjects_ProvisionedCo~",
                table: "PendingPasswordChanges");

            migrationBuilder.DropIndex(
                name: "IX_PendingPasswordChanges_ProvisionedConnectedSystemObjectId",
                table: "PendingPasswordChanges");

            migrationBuilder.DropColumn(
                name: "ProvisionedConnectedSystemObjectId",
                table: "PendingPasswordChanges");
        }
    }
}
