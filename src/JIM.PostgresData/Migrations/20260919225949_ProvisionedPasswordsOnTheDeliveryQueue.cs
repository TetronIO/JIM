using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ProvisionedPasswordsOnTheDeliveryQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingInitialPasswords");

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedPassword",
                table: "PendingPasswordChanges",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "SyncRuleId",
                table: "PendingPasswordChanges",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingPasswordChanges_SyncRuleId",
                table: "PendingPasswordChanges",
                column: "SyncRuleId",
                filter: "\"SyncRuleId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingPasswordChanges_SyncRules_SyncRuleId",
                table: "PendingPasswordChanges",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // The initial-password work list this setting governed is retired along with the table it trimmed;
            // provisioned passwords now live on the Password Synchronisation queue, whose own retention period
            // covers them.
            migrationBuilder.Sql("""DELETE FROM "ServiceSettingItems" WHERE "Key" = 'History.InitialPasswordRetentionPeriod';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingPasswordChanges_SyncRules_SyncRuleId",
                table: "PendingPasswordChanges");

            migrationBuilder.DropIndex(
                name: "IX_PendingPasswordChanges_SyncRuleId",
                table: "PendingPasswordChanges");

            migrationBuilder.DropColumn(
                name: "SyncRuleId",
                table: "PendingPasswordChanges");

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedPassword",
                table: "PendingPasswordChanges",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "PendingInitialPasswords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<int>(type: "integer", nullable: true),
                    LastAttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TargetMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingInitialPasswords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingInitialPasswords_ConnectedSystemObjects_ConnectedSys~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingInitialPasswords_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitialPasswords_ConnectedSystemId_Status",
                table: "PendingInitialPasswords",
                columns: new[] { "ConnectedSystemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitialPasswords_ConnectedSystemObjectId_Unique",
                table: "PendingInitialPasswords",
                column: "ConnectedSystemObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitialPasswords_SyncRuleId",
                table: "PendingInitialPasswords",
                column: "SyncRuleId");
        }
    }
}
