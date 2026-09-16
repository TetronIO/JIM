using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RenameScheduledIdentityDeletionActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Renames the housekeeping Activity's display name (#1668); the enum member (MetaverseObjectHousekeeping)
            // is unaffected, only the stored TargetName used for display.
            migrationBuilder.Sql(
                "UPDATE \"Activities\" SET \"TargetName\" = 'Scheduled Metaverse Object Deletion' WHERE \"TargetName\" = 'Scheduled Identity Deletion';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"Activities\" SET \"TargetName\" = 'Scheduled Identity Deletion' WHERE \"TargetName\" = 'Scheduled Metaverse Object Deletion';");
        }
    }
}
