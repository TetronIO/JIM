using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class DropTotalProvisionedColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Merge existing TotalProvisioned counts into TotalExported before dropping the column.
            // "Provisioned" was an export-phase concept that has been consolidated into "Exported".
            migrationBuilder.Sql("""
                UPDATE "Activities"
                SET "TotalExported" = "TotalExported" + "TotalProvisioned"
                WHERE "TotalProvisioned" > 0;
                """);

            migrationBuilder.DropColumn(
                name: "TotalProvisioned",
                table: "Activities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TotalProvisioned",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
