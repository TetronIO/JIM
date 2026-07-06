using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddExampleDataSetAuditingAndDataGenerationActivityType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "ExampleDataSets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "ExampleDataSets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByType",
                table: "ExampleDataSets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdated",
                table: "ExampleDataSets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUpdatedById",
                table: "ExampleDataSets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdatedByName",
                table: "ExampleDataSets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUpdatedByType",
                table: "ExampleDataSets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Reclassify existing data-generation run activities onto the new DataGeneration target type (21). Before
            // this change, a generation run was recorded as ExampleDataTemplate (1) + Execute (5); ExampleDataTemplate
            // is now a configuration-change target (its category moved to Configuration), so leaving historical runs
            // typed as ExampleDataTemplate would wrongly surface them under the Configuration Activities filter. Only
            // Execute-typed rows are runs; a template configuration change never carried this target type before now.
            migrationBuilder.Sql(
                "UPDATE \"Activities\" SET \"TargetType\" = 21 WHERE \"TargetType\" = 1 AND \"TargetOperationType\" = 5;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert the DataGeneration reclassification (see Up): move generation runs back to ExampleDataTemplate.
            migrationBuilder.Sql(
                "UPDATE \"Activities\" SET \"TargetType\" = 1 WHERE \"TargetType\" = 21;");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "ExampleDataSets");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "ExampleDataSets");

            migrationBuilder.DropColumn(
                name: "CreatedByType",
                table: "ExampleDataSets");

            migrationBuilder.DropColumn(
                name: "LastUpdated",
                table: "ExampleDataSets");

            migrationBuilder.DropColumn(
                name: "LastUpdatedById",
                table: "ExampleDataSets");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByName",
                table: "ExampleDataSets");

            migrationBuilder.DropColumn(
                name: "LastUpdatedByType",
                table: "ExampleDataSets");
        }
    }
}
