using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowUpReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PendingPasswordChanges_ActivityId",
                table: "PendingPasswordChanges",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExchangeChanges_ActivityId_Unique",
                table: "PendingExchangeChanges",
                column: "ActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ParentActivityId",
                table: "Activities",
                column: "ParentActivityId",
                filter: "\"ParentActivityId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingPasswordChanges_ActivityId",
                table: "PendingPasswordChanges");

            migrationBuilder.DropIndex(
                name: "IX_PendingExchangeChanges_ActivityId_Unique",
                table: "PendingExchangeChanges");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ParentActivityId",
                table: "Activities");
        }
    }
}
