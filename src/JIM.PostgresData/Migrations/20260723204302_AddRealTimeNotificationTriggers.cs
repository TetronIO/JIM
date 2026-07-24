using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// Real-time notification triggers (issue #307). Publishes PostgreSQL NOTIFY events when Worker Tasks
    /// change and when Activity progress changes, so services and the UI can react instantly instead of
    /// polling. NOTIFY is delivered on transaction commit, so listeners never observe uncommitted state.
    /// Payloads carry identifiers only (well within the 8000 byte NOTIFY limit); consumers re-query the
    /// database for current state. Channel names must match Constants.NotificationChannels in JIM.Models.
    /// </summary>
    public partial class AddRealTimeNotificationTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Worker Task lifecycle: INSERT (queued), status UPDATE (processing / cancellation requested /
            // step transition) and DELETE (terminal completion or cancellation; JIM removes Worker Tasks on
            // completion and the Activity record carries the outcome).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jim_notify_worker_task_change() RETURNS trigger AS $$
                DECLARE
                    affected_row record;
                BEGIN
                    IF (TG_OP = 'DELETE') THEN
                        affected_row := OLD;
                    ELSE
                        affected_row := NEW;
                    END IF;
                    PERFORM pg_notify('jim_worker_task_change', json_build_object(
                        'op', TG_OP,
                        'taskId', affected_row."Id",
                        'scheduleExecutionId', affected_row."ScheduleExecutionId",
                        'status', affected_row."Status")::text);
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_worker_tasks_notify_insert_delete
                AFTER INSERT OR DELETE ON "WorkerTasks"
                FOR EACH ROW EXECUTE FUNCTION jim_notify_worker_task_change();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_worker_tasks_notify_update
                AFTER UPDATE ON "WorkerTasks"
                FOR EACH ROW
                WHEN (OLD."Status" IS DISTINCT FROM NEW."Status")
                EXECUTE FUNCTION jim_notify_worker_task_change();
                """);

            // Activity progress: fires only when the columns the UI renders during execution change,
            // avoiding spurious notifications from unrelated Activity updates. Payload is the Activity id.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jim_notify_activity_progress() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('jim_activity_progress', NEW."Id"::text);
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_activities_notify_progress
                AFTER UPDATE ON "Activities"
                FOR EACH ROW
                WHEN (OLD."Status" IS DISTINCT FROM NEW."Status"
                    OR OLD."ObjectsProcessed" IS DISTINCT FROM NEW."ObjectsProcessed"
                    OR OLD."ObjectsToProcess" IS DISTINCT FROM NEW."ObjectsToProcess"
                    OR OLD."Message" IS DISTINCT FROM NEW."Message")
                EXECUTE FUNCTION jim_notify_activity_progress();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_activities_notify_progress ON "Activities";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jim_notify_activity_progress();");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_worker_tasks_notify_update ON "WorkerTasks";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_worker_tasks_notify_insert_delete ON "WorkerTasks";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jim_notify_worker_task_change();");
        }
    }
}
