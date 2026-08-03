using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthoritativeSourceTriggerModeAndDeletionPolicySnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeletionTriggerMode",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeletionPolicySnapshotJson",
                table: "MetaverseObjects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionTriggeredBySystemId",
                table: "MetaverseObjects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionTriggeredBySystemName",
                table: "MetaverseObjects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionPolicySnapshotJson",
                table: "ActivityRunProfileExecutionItems",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletionTriggerMode",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "DeletionPolicySnapshotJson",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "DeletionTriggeredBySystemId",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "DeletionTriggeredBySystemName",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "DeletionPolicySnapshotJson",
                table: "ActivityRunProfileExecutionItems");
        }
    }
}
