using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// The Password Delivery Service (#1635, layer 2 of the One Password Pipeline plan).
    /// <para>
    /// Three things in one migration. The Password Synchronisation queue gains a claim (ClaimedAt, ClaimedBy) so a
    /// deliverer can take rows with FOR UPDATE SKIP LOCKED and hold them under a lease. The queue table gains a
    /// notification trigger so the service is woken by a row change rather than by a poll; it follows the pattern
    /// of AddRealTimeNotificationTriggers, and its channel name must match Constants.NotificationChannels. And the
    /// PasswordDeliveryWorkerTask the service replaces is removed: its queued rows are deleted (nothing will ever
    /// run them; their Activities are left as history) and its discriminator column dropped.
    /// </para>
    /// </summary>
    public partial class AddPasswordDeliveryClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows first, then the column: a queued Password Delivery task left in the table would surface in the
            // Worker Tasks list as a type nothing recognises. The Discriminator holds the CLR type name.
            migrationBuilder.Sql("""DELETE FROM "WorkerTasks" WHERE "Discriminator" = 'PasswordDeliveryWorkerTask';""");

            migrationBuilder.DropColumn(
                name: "PasswordDeliveryWorkerTask_ConnectedSystemId",
                table: "WorkerTasks");

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "PendingPasswordChanges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "PendingPasswordChanges",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Every change to the queue wakes the service: an insert (a change queued), an update (a retry made
            // due, a hold released, an attempt recorded that schedules the next) and a delete (delivered). The
            // payload is the Connected System id, the unit the service runs lanes by; it re-reads the queue rather
            // than trusting the payload, as every consumer of these channels does. NOTIFY is delivered on commit.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jim_notify_password_change() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('jim_password_change', COALESCE(NEW."ConnectedSystemId", OLD."ConnectedSystemId")::text);
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_pending_password_changes_notify
                AFTER INSERT OR UPDATE OR DELETE ON "PendingPasswordChanges"
                FOR EACH ROW EXECUTE FUNCTION jim_notify_password_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_pending_password_changes_notify ON "PendingPasswordChanges";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jim_notify_password_change();");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "PendingPasswordChanges");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "PendingPasswordChanges");

            migrationBuilder.AddColumn<int>(
                name: "PasswordDeliveryWorkerTask_ConnectedSystemId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);
        }
    }
}
