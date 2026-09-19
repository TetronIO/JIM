using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RenamePolicyOverrideSignalAddDiscoveryOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "FineGrainedPolicySignal",
                table: "ConnectedSystemPasswordPolicies",
                newName: "PolicyOverrideSignal");

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryOutcome",
                table: "ConnectedSystemPasswordPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "FurtherChecksApply",
                table: "ConnectedSystemPasswordPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveryOutcome",
                table: "ConnectedSystemPasswordPolicies");

            migrationBuilder.DropColumn(
                name: "FurtherChecksApply",
                table: "ConnectedSystemPasswordPolicies");

            migrationBuilder.RenameColumn(
                name: "PolicyOverrideSignal",
                table: "ConnectedSystemPasswordPolicies",
                newName: "FineGrainedPolicySignal");
        }
    }
}
