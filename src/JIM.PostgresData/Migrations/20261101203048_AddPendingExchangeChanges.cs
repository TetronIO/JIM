using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// The Exchange queue (#2021, Phase 4 of the Exchange mailbox provisioning plan): one change per directory account,
    /// staged after a successful export and delivered by the Exchange delivery lane. The table gains a notification
    /// trigger so the lane is woken by a row change rather than by a poll, following AddPasswordDeliveryClaims; its
    /// channel name must match Constants.NotificationChannels.ExchangeChange.
    /// </summary>
    public partial class AddPendingExchangeChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingExchangeChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true),
                    RecipientType = table.Column<string>(type: "text", nullable: false),
                    Alias = table.Column<string>(type: "text", nullable: true),
                    Database = table.Column<string>(type: "text", nullable: true),
                    PrimarySmtpAddress = table.Column<string>(type: "text", nullable: true),
                    ExternalAddress = table.Column<string>(type: "text", nullable: true),
                    DomainController = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FailureKind = table.Column<int>(type: "integer", nullable: true),
                    FailureMessage = table.Column<string>(type: "text", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstFailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledById = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledByName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingExchangeChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingExchangeChanges_ConnectedSystemObjects_ConnectedSyst~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PendingExchangeChanges_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingExchangeChanges_MetaverseObjects_MetaverseObjectId",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PendingExchangeChanges_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingExchangeChanges_ConnectedSystemId_Status_NextRetryAt",
                table: "PendingExchangeChanges",
                columns: new[] { "ConnectedSystemId", "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingExchangeChanges_ConnectedSystemObjectId_Unique",
                table: "PendingExchangeChanges",
                column: "ConnectedSystemObjectId",
                unique: true,
                filter: "\"ConnectedSystemObjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExchangeChanges_MetaverseObjectId",
                table: "PendingExchangeChanges",
                column: "MetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExchangeChanges_SyncRuleId",
                table: "PendingExchangeChanges",
                column: "SyncRuleId");

            // Every change to the queue wakes the lane: an insert or supersession (a change staged), an update (a retry
            // made due, an attempt recorded that schedules the next) and a delete (delivered or withdrawn). The payload
            // is the Connected System id, the unit the lane runs by; the lane re-reads the queue rather than trusting
            // the payload, as every consumer of these channels does. NOTIFY is delivered on commit.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jim_notify_exchange_change() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('jim_exchange_change', COALESCE(NEW."ConnectedSystemId", OLD."ConnectedSystemId")::text);
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_pending_exchange_changes_notify
                AFTER INSERT OR UPDATE OR DELETE ON "PendingExchangeChanges"
                FOR EACH ROW EXECUTE FUNCTION jim_notify_exchange_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_pending_exchange_changes_notify ON "PendingExchangeChanges";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jim_notify_exchange_change();");

            migrationBuilder.DropTable(
                name: "PendingExchangeChanges");
        }
    }
}
