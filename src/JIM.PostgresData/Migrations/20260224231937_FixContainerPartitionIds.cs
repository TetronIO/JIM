using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class FixContainerPartitionIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fix data corruption: child containers should not have PartitionId set.
            // Only root containers (those with no ParentContainerId) should reference a partition.
            // A bug in RefreshAndAutoSelectContainersAsync was setting Partition on all new containers
            // including children, causing them to appear as duplicates at the partition root level.
            migrationBuilder.Sql(
                """
                UPDATE "ConnectedSystemContainers"
                SET "PartitionId" = NULL
                WHERE "ParentContainerId" IS NOT NULL
                  AND "PartitionId" IS NOT NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No rollback — the original PartitionId values on child containers were incorrect data
        }
    }
}
