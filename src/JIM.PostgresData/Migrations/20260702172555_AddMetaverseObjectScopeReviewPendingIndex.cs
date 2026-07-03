using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaverseObjectScopeReviewPendingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjects_ScopeReviewPending",
                table: "MetaverseObjects",
                column: "ScopeReviewPending",
                filter: "\"ScopeReviewPending\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjects_ScopeReviewPending",
                table: "MetaverseObjects");
        }
    }
}
