using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <summary>
    /// One password operation (#1635, layer 3 of the One Password Pipeline plan). The Password Synchronisation
    /// queue gains an Origin (Propagated or Explicit) and an EnableAccount decision, so an administrator's Set
    /// Password travels through the same queue as a propagated change and is delivered even where the Connected
    /// System is not configured for Password Synchronisation. Every existing row was a propagated change, which
    /// is what the Origin default of 0 records; EnableAccount stays null for those, as it always does for a
    /// propagated password.
    /// </summary>
    public partial class AddPasswordChangeOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableAccount",
                table: "PendingPasswordChanges",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "PendingPasswordChanges",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAccount",
                table: "PendingPasswordChanges");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "PendingPasswordChanges");
        }
    }
}
