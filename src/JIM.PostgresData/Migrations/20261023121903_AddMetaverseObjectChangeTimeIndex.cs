using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMetaverseObjectChangeTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_ChangeTime",
                table: "MetaverseObjectChanges",
                column: "ChangeTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectChanges_ChangeTime",
                table: "MetaverseObjectChanges");
        }
    }
}
