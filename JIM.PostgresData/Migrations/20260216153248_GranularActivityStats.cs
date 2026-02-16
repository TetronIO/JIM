using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class GranularActivityStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add AttributeFlowCount to RPEIs
            migrationBuilder.AddColumn<int>(
                name: "AttributeFlowCount",
                table: "ActivityRunProfileExecutionItems",
                type: "integer",
                nullable: true);

            // 2. Add all new granular columns to Activities (default 0)
            migrationBuilder.AddColumn<int>(
                name: "TotalAdded",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalUpdated",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalDeleted",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalProjected",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalJoined",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalAttributeFlows",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalDisconnected",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalDisconnectedOutOfScope",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalOutOfScopeRetainJoin",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalDriftCorrections",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalProvisioned",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalExported",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalDeprovisioned",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalCreated",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalPendingExports",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalErrors",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 3. Migrate existing aggregate data to granular fields based on ConnectedSystemRunType.
            // ConnectedSystemRunType enum: 0=NotSet, 1=FullImport, 2=DeltaImport, 3=FullSync, 4=DeltaSync, 5=Export
            migrationBuilder.Sql("""
                -- Import runs (FullImport=1, DeltaImport=2): Creates->Added, Updates->Updated, Deletes->Deleted
                UPDATE "Activities" SET
                    "TotalAdded" = "TotalObjectCreates",
                    "TotalUpdated" = "TotalObjectUpdates",
                    "TotalDeleted" = "TotalObjectDeletes",
                    "TotalErrors" = "TotalObjectErrors"
                WHERE "ConnectedSystemRunType" IN (1, 2);

                -- Sync runs (FullSync=3, DeltaSync=4): Creates->Projected, Updates->Joined, Flows->AttributeFlows, Deletes->Disconnected
                UPDATE "Activities" SET
                    "TotalProjected" = "TotalObjectCreates",
                    "TotalJoined" = "TotalObjectUpdates",
                    "TotalAttributeFlows" = "TotalObjectFlows",
                    "TotalDisconnected" = "TotalObjectDeletes",
                    "TotalErrors" = "TotalObjectErrors"
                WHERE "ConnectedSystemRunType" IN (3, 4);

                -- Export runs (Export=5): Creates->Provisioned, Updates->Exported, Deletes->Deprovisioned
                UPDATE "Activities" SET
                    "TotalProvisioned" = "TotalObjectCreates",
                    "TotalExported" = "TotalObjectUpdates",
                    "TotalDeprovisioned" = "TotalObjectDeletes",
                    "TotalErrors" = "TotalObjectErrors"
                WHERE "ConnectedSystemRunType" = 5;

                -- Activities without a run type (NotSet=0, or NULL): just migrate errors
                UPDATE "Activities" SET
                    "TotalErrors" = "TotalObjectErrors"
                WHERE "ConnectedSystemRunType" IS NULL OR "ConnectedSystemRunType" = 0;

                -- Data generation activities: TotalObjectCreates -> TotalCreated
                -- These have TargetType = DataGenerationTemplate (enum value 1)
                UPDATE "Activities" SET
                    "TotalCreated" = "TotalObjectCreates"
                WHERE "TargetType" = 1 AND "TotalObjectCreates" > 0;
                """);

            // 4. Drop old aggregate columns
            migrationBuilder.DropColumn(
                name: "TotalObjectCreates",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "TotalObjectUpdates",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "TotalObjectFlows",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "TotalObjectDeletes",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "TotalObjectErrors",
                table: "Activities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore old aggregate columns
            migrationBuilder.AddColumn<int>(
                name: "TotalObjectCreates",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalObjectUpdates",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalObjectFlows",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalObjectDeletes",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalObjectErrors",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Drop new granular columns
            migrationBuilder.DropColumn(name: "TotalAdded", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalUpdated", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalDeleted", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalProjected", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalJoined", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalAttributeFlows", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalDisconnected", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalDisconnectedOutOfScope", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalOutOfScopeRetainJoin", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalDriftCorrections", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalProvisioned", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalExported", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalDeprovisioned", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalCreated", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalPendingExports", table: "Activities");
            migrationBuilder.DropColumn(name: "TotalErrors", table: "Activities");

            migrationBuilder.DropColumn(
                name: "AttributeFlowCount",
                table: "ActivityRunProfileExecutionItems");
        }
    }
}
