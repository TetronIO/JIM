using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddScimRequestSummaryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Activities_ScimRequestAggregation_Unique",
                table: "Activities",
                columns: new[] { "InitiatedById", "ConnectedSystemId", "AggregationWindowStart" },
                unique: true,
                filter: "\"TargetType\" = 26 AND \"AggregationWindowStart\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_ScimRequestAggregation_Unique",
                table: "Activities");
        }
    }
}
