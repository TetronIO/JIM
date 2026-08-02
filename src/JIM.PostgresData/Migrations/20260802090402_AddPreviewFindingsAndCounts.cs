using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviewFindingsAndCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written rather than scaffolded: PostgreSQL has no implicit text-to-jsonb cast, so the
            // ALTER COLUMN ... TYPE jsonb that EF generates is rejected outright, with or without rows in the
            // table. The USING clause is what makes it a conversion rather than a request.
            migrationBuilder.Sql(
                """ALTER TABLE "ConfigurationChangePreviews" ALTER COLUMN "ProposedConfigurationSnapshot" TYPE jsonb USING "ProposedConfigurationSnapshot"::jsonb;""");

            migrationBuilder.AddColumn<string>(
                name: "ImpactCounts",
                table: "ConfigurationChangePreviews",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationFindings",
                table: "ConfigurationChangePreviews",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImpactCounts",
                table: "ConfigurationChangePreviews");

            migrationBuilder.DropColumn(
                name: "ValidationFindings",
                table: "ConfigurationChangePreviews");

            migrationBuilder.Sql(
                """ALTER TABLE "ConfigurationChangePreviews" ALTER COLUMN "ProposedConfigurationSnapshot" TYPE text USING "ProposedConfigurationSnapshot"::text;""");
        }
    }
}
