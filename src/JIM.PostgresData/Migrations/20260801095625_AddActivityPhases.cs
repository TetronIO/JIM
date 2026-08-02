using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityPhases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityPhases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParentKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Started = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Ended = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityPhases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityPhases_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityPhases_ActivityId_Key",
                table: "ActivityPhases",
                columns: new[] { "ActivityId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityPhases_ActivityId_Order",
                table: "ActivityPhases",
                columns: new[] { "ActivityId", "Order" });

            // A phase transition is progress the portal must see, but it writes no Activity column,
            // so the Activities trigger added by AddRealTimeNotificationTriggers (#307) would not
            // fire for it. Publish on the same channel with the same payload (the Activity id), so
            // existing listeners simply re-query and pick the phases up. Seeding a run's phases
            // inserts several rows in one transaction; PostgreSQL collapses identical (channel,
            // payload) notifications within a transaction, so that arrives as one wake-up.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jim_notify_activity_phase_change() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('jim_activity_progress', NEW."ActivityId"::text);
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_activity_phases_notify_insert
                AFTER INSERT ON "ActivityPhases"
                FOR EACH ROW EXECUTE FUNCTION jim_notify_activity_phase_change();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_activity_phases_notify_update
                AFTER UPDATE ON "ActivityPhases"
                FOR EACH ROW
                WHEN (OLD."Status" IS DISTINCT FROM NEW."Status"
                    OR OLD."Started" IS DISTINCT FROM NEW."Started"
                    OR OLD."Ended" IS DISTINCT FROM NEW."Ended")
                EXECUTE FUNCTION jim_notify_activity_phase_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_activity_phases_notify_update ON "ActivityPhases";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_activity_phases_notify_insert ON "ActivityPhases";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jim_notify_activity_phase_change();");

            migrationBuilder.DropTable(
                name: "ActivityPhases");
        }
    }
}
